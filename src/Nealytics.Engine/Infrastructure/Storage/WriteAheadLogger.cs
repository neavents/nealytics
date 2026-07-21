namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

public sealed class WriteAheadLogger : IAsyncDisposable
{
    private readonly string _logFilePath;
    private readonly string _sealedFilePath;
    private readonly FileStream _fileStream;
    private readonly SemaphoreSlim _fileLock;
    private readonly Channel<PendingAppend> _pending;
    private readonly Task _flushLoop;
    private long _uncommittedRecords;
    private long _flushGroupCount;
    private long _flushedRecordCount;
    private long _flushDurationTicks;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _threadBuffer;

    public WriteAheadLogger(IOptions<TelemetryEngineOptions> options)
    {
        TelemetryEngineOptions engineOptions = options.Value;
        string directory = engineOptions.WriteAheadLogDirectory;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _logFilePath = Path.Combine(directory, "telemetry_wal.log");
        _sealedFilePath = Path.Combine(directory, "telemetry_wal.replay");

        SealExistingLogForRecovery();

        int bufferSize = engineOptions.WalFileBufferBytes > 0 ? engineOptions.WalFileBufferBytes : 65_536;

        _fileStream = new FileStream(
            _logFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous);

        _fileLock = new SemaphoreSlim(1, 1);
        _uncommittedRecords = 0;

        _pending = Channel.CreateUnbounded<PendingAppend>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        _flushLoop = Task.Run(RunFlushLoopAsync);
    }

    public long UncommittedRecordCount => Interlocked.Read(ref _uncommittedRecords);

    internal long FlushGroupCount => Interlocked.Read(ref _flushGroupCount);
    internal long FlushedRecordCount => Interlocked.Read(ref _flushedRecordCount);
    internal double AverageGroupSize
    {
        get
        {
            long groups = Interlocked.Read(ref _flushGroupCount);
            return groups == 0 ? 0 : (double)Interlocked.Read(ref _flushedRecordCount) / groups;
        }
    }
    internal double AverageFlushMilliseconds
    {
        get
        {
            long groups = Interlocked.Read(ref _flushGroupCount);
            if (groups == 0)
            {
                return 0;
            }
            double seconds = (double)Interlocked.Read(ref _flushDurationTicks) / Stopwatch.Frequency;
            return seconds / groups * 1000.0;
        }
    }

    private void SealExistingLogForRecovery()
    {
        if (!File.Exists(_logFilePath) || new FileInfo(_logFilePath).Length == 0)
        {
            return;
        }

        if (File.Exists(_sealedFilePath))
        {
            using FileStream source = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.None);
            using FileStream destination = new FileStream(_sealedFilePath, FileMode.Append, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
            source.Close();
            File.Delete(_logFilePath);
            return;
        }

        File.Move(_logFilePath, _sealedFilePath);
    }

    public async Task AppendAsync(GlobalTelemetryPayload payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ArrayBufferWriter<byte> buffer = _threadBuffer ??= new ArrayBufferWriter<byte>(512);
        buffer.ResetWrittenCount();

        using (Utf8JsonWriter jsonWriter = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true }))
        {
            JsonSerializer.Serialize(jsonWriter, payload, TelemetryAotContext.Default.GlobalTelemetryPayload);
        }

        ReadOnlySpan<byte> written = buffer.WrittenSpan;
        int length = written.Length + 1;
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        written.CopyTo(rented);
        rented[written.Length] = (byte)'\n';

        PendingAppend pending = new PendingAppend(rented, length);
        await _pending.Writer.WriteAsync(pending, cancellationToken);
        await pending.Completion.Task.WaitAsync(cancellationToken);
    }

    private async Task RunFlushLoopAsync()
    {
        ChannelReader<PendingAppend> reader = _pending.Reader;
        List<PendingAppend> group = new List<PendingAppend>(256);

        while (await reader.WaitToReadAsync())
        {
            group.Clear();
            while (reader.TryRead(out PendingAppend? item))
            {
                group.Add(item);
            }

            await FlushGroupAsync(group);
        }
    }

    private async Task FlushGroupAsync(List<PendingAppend> group)
    {
        if (group.Count == 0)
        {
            return;
        }

        long flushStartTicks = Stopwatch.GetTimestamp();
        await _fileLock.WaitAsync();
        try
        {
            for (int i = 0; i < group.Count; i++)
            {
                PendingAppend entry = group[i];
                await _fileStream.WriteAsync(entry.Buffer.AsMemory(0, entry.Length));
            }

            await _fileStream.FlushAsync();
            _fileStream.Flush(true);

            Interlocked.Add(ref _uncommittedRecords, group.Count);
            Interlocked.Increment(ref _flushGroupCount);
            Interlocked.Add(ref _flushedRecordCount, group.Count);
            Interlocked.Add(ref _flushDurationTicks, Stopwatch.GetTimestamp() - flushStartTicks);
        }
        catch (Exception ex)
        {
            for (int i = 0; i < group.Count; i++)
            {
                PendingAppend entry = group[i];
                ArrayPool<byte>.Shared.Return(entry.Buffer);
                entry.Completion.TrySetException(ex);
            }
            return;
        }
        finally
        {
            _fileLock.Release();
        }

        for (int i = 0; i < group.Count; i++)
        {
            PendingAppend entry = group[i];
            ArrayPool<byte>.Shared.Return(entry.Buffer);
            entry.Completion.TrySetResult();
        }
    }

    public async Task AcknowledgeCommitAsync(int committedCount)
    {
        if (committedCount <= 0)
        {
            return;
        }

        await _fileLock.WaitAsync();
        try
        {
            long remaining = Interlocked.Add(ref _uncommittedRecords, -committedCount);
            if (remaining <= 0)
            {
                Interlocked.Exchange(ref _uncommittedRecords, 0);
                _fileStream.SetLength(0);
                await _fileStream.FlushAsync();
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task TruncateAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            _fileStream.SetLength(0);
            await _fileStream.FlushAsync();
            Interlocked.Exchange(ref _uncommittedRecords, 0);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public bool HasSealedSegment => File.Exists(_sealedFilePath) && new FileInfo(_sealedFilePath).Length > 0;

    public async Task<IReadOnlyList<GlobalTelemetryPayload>> ReplayUncommittedAsync()
    {
        List<GlobalTelemetryPayload> recovered = new List<GlobalTelemetryPayload>();

        if (!File.Exists(_sealedFilePath))
        {
            return recovered;
        }

        using FileStream readStream = new FileStream(_sealedFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new StreamReader(readStream);

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                GlobalTelemetryPayload? payload = JsonSerializer.Deserialize(
                    line,
                    TelemetryAotContext.Default.GlobalTelemetryPayload);

                if (payload is not null
                    && !string.IsNullOrEmpty(payload.ProjectId)
                    && !string.IsNullOrEmpty(payload.TenantId)
                    && !string.IsNullOrEmpty(payload.SessionId)
                    && !string.IsNullOrEmpty(payload.EventType))
                {
                    recovered.Add(payload);
                }
            }
            catch (JsonException)
            {
            }
        }
        return recovered;
    }

    public Task DeleteSealedSegmentAsync()
    {
        if (File.Exists(_sealedFilePath))
        {
            File.Delete(_sealedFilePath);
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();

        try
        {
            await _flushLoop;
        }
        catch
        {
        }

        await _fileStream.DisposeAsync();
        _fileLock.Dispose();
    }

    private sealed class PendingAppend
    {
        public PendingAppend(byte[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public byte[] Buffer { get; }
        public int Length { get; }
        public TaskCompletionSource Completion { get; }
    }
}

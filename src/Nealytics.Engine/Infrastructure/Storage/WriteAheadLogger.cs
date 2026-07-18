namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

public sealed class WriteAheadLogger : IAsyncDisposable
{
    private readonly string _logFilePath;
    private readonly string _sealedFilePath;
    private readonly FileStream _fileStream;
    private readonly SemaphoreSlim _lock;
    private long _uncommittedRecords;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _threadBuffer;

    public WriteAheadLogger(IOptions<TelemetryEngineOptions> options)
    {
        string directory = options.Value.WriteAheadLogDirectory;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _logFilePath = Path.Combine(directory, "telemetry_wal.log");
        _sealedFilePath = Path.Combine(directory, "telemetry_wal.replay");

        SealExistingLogForRecovery();

        _fileStream = new FileStream(
            _logFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough | FileOptions.Asynchronous);

        _lock = new SemaphoreSlim(1, 1);
        _uncommittedRecords = 0;
    }

    public long UncommittedRecordCount => Interlocked.Read(ref _uncommittedRecords);

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
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ArrayBufferWriter<byte> buffer = _threadBuffer ??= new ArrayBufferWriter<byte>(512);
            buffer.ResetWrittenCount();

            using (Utf8JsonWriter jsonWriter = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true }))
            {
                JsonSerializer.Serialize(jsonWriter, payload, TelemetryAotContext.Default.GlobalTelemetryPayload);
            }

            buffer.GetSpan(1)[0] = (byte)'\n';
            buffer.Advance(1);

            await _fileStream.WriteAsync(buffer.WrittenMemory, cancellationToken);
            _uncommittedRecords++;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AcknowledgeCommitAsync(int committedCount)
    {
        if (committedCount <= 0)
        {
            return;
        }

        await _lock.WaitAsync();
        try
        {
            _uncommittedRecords -= committedCount;
            if (_uncommittedRecords <= 0)
            {
                _uncommittedRecords = 0;
                _fileStream.SetLength(0);
                await _fileStream.FlushAsync();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task TruncateAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _fileStream.SetLength(0);
            await _fileStream.FlushAsync();
            _uncommittedRecords = 0;
        }
        finally
        {
            _lock.Release();
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
        await _lock.WaitAsync();
        try
        {
            await _fileStream.DisposeAsync();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}

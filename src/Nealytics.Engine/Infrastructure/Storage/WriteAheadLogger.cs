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
    private readonly FileStream _fileStream;
    private readonly SemaphoreSlim _lock;
    private long _appendedBytesSinceLastTruncate;

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
        _fileStream = new FileStream(
            _logFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough | FileOptions.Asynchronous);

        _lock = new SemaphoreSlim(1, 1);
        _appendedBytesSinceLastTruncate = 0;
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
            Interlocked.Add(ref _appendedBytesSinceLastTruncate, buffer.WrittenCount);
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
            Interlocked.Exchange(ref _appendedBytesSinceLastTruncate, 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task TruncateIfSafeAsync()
    {
        if (Interlocked.Read(ref _appendedBytesSinceLastTruncate) == 0)
        {
            return;
        }

        await _lock.WaitAsync();
        try
        {
            _fileStream.SetLength(0);
            await _fileStream.FlushAsync();
            Interlocked.Exchange(ref _appendedBytesSinceLastTruncate, 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    public IReadOnlyList<GlobalTelemetryPayload> ReplayUncommitted()
    {
        List<GlobalTelemetryPayload> recovered = new List<GlobalTelemetryPayload>();
        string readPath = _logFilePath;

        if (!File.Exists(readPath))
        {
            return recovered;
        }

        using FileStream readStream = new FileStream(readPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new StreamReader(readStream);

        while (reader.ReadLine() is { } line)
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
                    && payload.ProjectId.Length > 0
                    && payload.TenantId.Length > 0
                    && payload.SessionId.Length > 0)
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

namespace Nealytics.Engine.Infrastructure.Storage;

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Serialization;

public sealed partial class WriteAheadLogger
{
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

}

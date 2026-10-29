namespace Nealytics.Engine.Infrastructure.Storage;

using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage.Wal;

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
            {
                using FileStream source = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.None);
                using FileStream destination = new FileStream(_sealedFilePath, FileMode.Append, FileAccess.Write, FileShare.None);
                source.CopyTo(destination);
            }
            File.Delete(_logFilePath);
            return;
        }

        File.Move(_logFilePath, _sealedFilePath);
    }

    public bool HasSealedSegment => File.Exists(_sealedFilePath) && new FileInfo(_sealedFilePath).Length > 0;

    public async Task<IReadOnlyList<GlobalTelemetryPayload>> ReplayUncommittedAsync()
    {
        List<GlobalTelemetryPayload> recovered = new List<GlobalTelemetryPayload>();
        await foreach (WalReplayRecord record in ReplayRecordsAsync())
        {
            recovered.Add(record.Payload);
        }
        return recovered;
    }

    internal int ReplayBatchByteLimit => _limits.FlushBytes;

    internal async IAsyncEnumerable<WalReplayRecord> ReplayRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_sealedFilePath))
        {
            yield break;
        }
        await using FileStream source = new FileStream(
            _sealedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536,
            WalReplayReader.OpenOptions);
        WalReplayReader reader = new WalReplayReader(_limits.RecordBytes);
        await foreach (WalReplayRecord record in reader.ReadAsync(source, cancellationToken))
        {
            yield return record;
        }
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

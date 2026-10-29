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
    public bool HasSealedSegment
    {
        get
        {
            foreach (string path in _recoveryPaths)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

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
        WalReplayReader reader = new WalReplayReader(_limits.RecordBytes);
        foreach (string path in _recoveryPaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }
            await using FileStream source = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, WalReplayReader.OpenOptions);
            await foreach (WalReplayRecord record in reader.ReadAsync(source, cancellationToken))
            {
                yield return record;
            }
        }
    }

    public Task DeleteSealedSegmentAsync()
    {
        _directory.DeleteRecovery(_recoveryPaths);
        return Task.CompletedTask;
    }

}

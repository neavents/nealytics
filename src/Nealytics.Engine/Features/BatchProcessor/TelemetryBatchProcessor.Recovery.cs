namespace Nealytics.Engine.Features.BatchProcessor;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage.Wal;

public sealed partial class TelemetryBatchProcessor
{
    private async Task RecoverWalEntriesAsync(CancellationToken cancellationToken)
    {
        List<GlobalTelemetryPayload> buffer = _bufferA;
        int bytes = 0;
        long recovered = 0;
        try
        {
            await foreach (WalReplayRecord record in _wal.ReplayRecordsAsync(cancellationToken))
            {
                if ((long)bytes + record.FramedBytes > _wal.ReplayBatchByteLimit)
                {
                    await PushRecoveryBatchAsync(buffer, cancellationToken);
                    bytes = 0;
                }
                buffer.Add(record.Payload);
                bytes += record.FramedBytes;
                recovered++;
                if (buffer.Count >= _options.DatabaseBatchCommitSize)
                {
                    await PushRecoveryBatchAsync(buffer, cancellationToken);
                    bytes = 0;
                }
            }
            if (buffer.Count > 0)
            {
                await PushRecoveryBatchAsync(buffer, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await _wal.DeleteSealedSegmentAsync();
            LogWalRecovery(_logger, recovered);
        }
        finally
        {
            buffer.Clear();
        }
    }

    private async Task PushRecoveryBatchAsync(List<GlobalTelemetryPayload> buffer, CancellationToken cancellationToken)
    {
        while (!await PushBatchToClickHouseAsync(buffer, cancellationToken, replay: true))
        {
            LogWalReplayRetry(_logger, _options.WalReplayRetryDelayMs);
            await Task.Delay(_options.WalReplayRetryDelayMs, cancellationToken);
        }
    }
}

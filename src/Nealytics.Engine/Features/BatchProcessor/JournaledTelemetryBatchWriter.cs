namespace Nealytics.Engine.Features.BatchProcessor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Delivery;

internal sealed class JournaledTelemetryBatchWriter : ITelemetryBatchWriter, IDeliveryCheckpoint
{
    private readonly IDeliveryBatchWriter _writer;
    private readonly TelemetryDeliveryJournal _journal;
    private readonly WriteAheadLogger _wal;
    private readonly TimeSpan _retryAge;
    private readonly RollupRegistry? _rollups;
    private bool _loaded;
    private TelemetryDeliveryUnit? _pending;
    private ExceptionDispatchInfo? _preparationFailure;

    public JournaledTelemetryBatchWriter(
        IDeliveryBatchWriter writer,
        TelemetryDeliveryJournal journal,
        WriteAheadLogger wal,
        IOptions<TelemetryEngineOptions> options,
        RollupRegistry? rollups = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Value.DeliveryRetryMaxAgeSeconds, 1);
        if (options.Value.EnableAsyncInsert)
        {
            throw new InvalidOperationException("Replay-safe delivery requires EnableAsyncInsert=false on ClickHouse 25.3.");
        }
        _rollups = rollups;
        _writer = writer;
        _journal = journal;
        _wal = wal;
        _retryAge = TimeSpan.FromSeconds(options.Value.DeliveryRetryMaxAgeSeconds);
    }

    public async Task WriteAsync(
        IReadOnlyList<GlobalTelemetryPayload> batch, int count, CancellationToken cancellationToken)
    {
        _preparationFailure?.Throw();
        if (!_loaded)
        {
            foreach (TelemetryDeliveryUnit pending in _journal.Load())
            {
                await DeliverAsync(pending, cancellationToken);
            }
            _loaded = true;
        }
        List<GlobalTelemetryPayload> remaining = new List<GlobalTelemetryPayload>(count);
        for (int i = 0; i < count; i++)
        {
            if (!_journal.IsDelivered(batch[i]))
            {
                remaining.Add(batch[i]);
            }
        }
        if (remaining.Count == 0)
        {
            return;
        }
        if (_pending is null)
        {
            TelemetryDeliveryUnit unit = new TelemetryDeliveryUnit
            {
                SourceId = await _writer.GetSourceIdAsync(cancellationToken),
                Schema = _writer.SchemaFingerprint,
                Items = remaining.ToArray()
            };
            try
            {
                _pending = _journal.Prepare(unit);
            }
            catch (Exception error)
            {
                _preparationFailure = ExceptionDispatchInfo.Capture(error);
                throw;
            }
        }
        else
        {
            if (_pending.Items.Length != remaining.Count)
            {
                throw new InvalidDataException("An uncertain delivery must finish before another unit can be inserted.");
            }
            for (int i = 0; i < remaining.Count; i++)
            {
                if (_pending.Items[i].WalRecordId != remaining[i].WalRecordId)
                {
                    throw new InvalidDataException("An uncertain delivery cannot be regrouped.");
                }
            }
        }
        await DeliverAsync(_pending, cancellationToken);
        _pending = null;
    }

    public Task CheckpointAsync() => _wal.CheckpointDeliveryAsync(_journal.Clear);

    public Task CheckpointRecoveryAsync() => _wal.CheckpointDeliveryAsync(_journal.Clear, requireEmptyActive: false);

    private async Task DeliverAsync(TelemetryDeliveryUnit unit, CancellationToken cancellationToken)
    {
        if (unit.Schema != _writer.SchemaFingerprint
            || unit.SourceId != await _writer.GetSourceIdAsync(cancellationToken))
        {
            throw new InvalidDataException("Pending delivery storage or schema changed; restore its original declaration before replay.");
        }
        if (DateTimeOffset.UtcNow - unit.IngestedAt > _retryAge || unit.IngestedAt > DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("The delivery retry safety window expired; an offline reconciliation is required.");
        }
        try
        {
            await _writer.WriteDeliveryAsync(unit, cancellationToken);
            _journal.Complete(unit);
        }
        catch
        {
            if (_rollups is not null)
            {
                foreach (Rollup rollup in _rollups.Declared)
                {
                    _rollups.MarkUnroutable(rollup, "a delivery has an uncertain materialized-view outcome");
                }
            }
            throw;
        }
    }
}

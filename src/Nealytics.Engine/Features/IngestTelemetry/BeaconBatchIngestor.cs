namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;

internal readonly record struct BeaconIngestResult(int Accepted, int Rejected);

internal sealed class BeaconBatchIngestor
{
    private readonly WriteAheadLogger _wal;
    private readonly TelemetryChannelBroker _broker;
    private readonly IngestGate _gate;
    private readonly DimensionSanitizer _dimensions;
    private readonly MeasureSanitizer _measures;
    private readonly int _records;

    public BeaconBatchIngestor(
        WriteAheadLogger wal,
        TelemetryChannelBroker broker,
        IngestGate gate,
        DimensionSanitizer dimensions,
        MeasureSanitizer measures,
        IOptions<TelemetryEngineOptions> options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Value.BeaconWalBatchRecords, 1);
        _wal = wal;
        _broker = broker;
        _gate = gate;
        _dimensions = dimensions;
        _measures = measures;
        _records = Math.Min(options.Value.BeaconWalBatchRecords, options.Value.WalMaxFlushRecords);
        ArgumentOutOfRangeException.ThrowIfLessThan(_records, 1);
    }

    public async Task<BeaconIngestResult> IngestAsync(
        IAsyncEnumerable<GlobalTelemetryPayload?> payloads,
        IngestionKeyPolicy policy,
        DateTime receivedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(policy);
        List<GlobalTelemetryPayload> buffer = new List<GlobalTelemetryPayload>(_records);
        using CancellationTokenSource parsing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using IAsyncEnumerator<GlobalTelemetryPayload?> source = payloads.GetAsyncEnumerator(parsing.Token);
        ValueTask<bool> move = default;
        bool outstanding = false;
        int accepted = 0;
        int rejected = 0;
        try
        {
            while (true)
            {
                move = source.MoveNextAsync();
                outstanding = true;
                if (!move.IsCompleted && buffer.Count > 0)
                {
                    accepted += await FlushAsync(buffer, cancellationToken, cancellationToken);
                }
                outstanding = false;
                if (!await move)
                {
                    accepted += await FlushAsync(buffer, cancellationToken, cancellationToken);
                    return new BeaconIngestResult(accepted, rejected);
                }
                GlobalTelemetryPayload? payload = source.Current;
                if (!Accept(payload, policy, receivedAt))
                {
                    rejected++;
                    continue;
                }
                buffer.Add(payload!);
                if (buffer.Count == _records)
                {
                    accepted += await FlushAsync(buffer, cancellationToken, cancellationToken);
                }
            }
        }
        catch
        {
            if (buffer.Count > 0)
            {
                await FlushAsync(buffer, CancellationToken.None, cancellationToken);
            }
            throw;
        }
        finally
        {
            if (outstanding)
            {
                parsing.Cancel();
                try
                {
                    await move;
                }
                catch (Exception)
                {
                }
            }
            buffer.Clear();
        }
    }

    private bool Accept(GlobalTelemetryPayload? payload, IngestionKeyPolicy policy, DateTime receivedAt)
    {
        IngestRejection rejection = _gate.Check(policy, payload, receivedAt, out _);
        if (rejection != IngestRejection.None)
        {
            TelemetryDiagnostics.EventsRejected.Add(
                1,
                new KeyValuePair<string, object?>("reason", IngestValidation.Tag(rejection)),
                new KeyValuePair<string, object?>("transport", "beacon"));
            return false;
        }
        _dimensions.Sanitize(payload!);
        _measures.Sanitize(payload!);
        return true;
    }

    private async Task<int> FlushAsync(
        List<GlobalTelemetryPayload> buffer, CancellationToken persistenceToken, CancellationToken publishToken)
    {
        int offset = 0;
        try
        {
            while (offset < buffer.Count)
            {
                WalBatchAppendResult result = await _wal.AppendBatchAsync(buffer, persistenceToken, offset);
                int end = offset + result.Count;
                while (offset < end)
                {
                    await _broker.PublishAsync(buffer[offset], publishToken);
                    offset++;
                }
                result.ThrowIfFailed();
            }
            return offset;
        }
        finally
        {
            buffer.Clear();
        }
    }
}

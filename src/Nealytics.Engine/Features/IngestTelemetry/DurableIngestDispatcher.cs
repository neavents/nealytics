namespace Nealytics.Engine.Features.IngestTelemetry;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;

internal sealed class DurableIngestDispatcher
{
    private readonly WriteAheadLogger _wal;
    private readonly TelemetryChannelBroker _broker;
    private readonly IngestionRequestReader _reader;
    private readonly IHostApplicationLifetime _lifetime;

    public DurableIngestDispatcher(
        WriteAheadLogger wal,
        TelemetryChannelBroker broker,
        IngestionRequestReader reader,
        IHostApplicationLifetime lifetime)
    {
        _wal = wal;
        _broker = broker;
        _reader = reader;
        _lifetime = lifetime;
    }

    public async Task AppendAsync(GlobalTelemetryPayload payload, CancellationToken cancellationToken)
    {
        await _wal.AppendAsync(payload, cancellationToken);
        await _broker.PublishAsync(payload, PublishToken(cancellationToken));
    }

    public async Task<int> AppendBatchAsync(
        IReadOnlyList<GlobalTelemetryPayload> payloads,
        CancellationToken persistenceToken,
        CancellationToken publishToken)
    {
        int offset = 0;
        CancellationToken dispatchToken = PublishToken(publishToken);
        while (offset < payloads.Count)
        {
            WalBatchAppendResult result = await _wal.AppendBatchAsync(payloads, persistenceToken, offset);
            int end = offset + result.Count;
            while (offset < end)
            {
                await _broker.PublishAsync(payloads[offset], dispatchToken);
                offset++;
            }
            result.ThrowIfFailed();
        }
        return offset;
    }

    private CancellationToken PublishToken(CancellationToken callerToken) =>
        _reader.UsesCustomPath ? _lifetime.ApplicationStopping : callerToken;
}

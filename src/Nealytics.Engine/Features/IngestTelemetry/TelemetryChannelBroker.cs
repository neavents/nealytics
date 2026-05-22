namespace Nealytics.Engine.Features.IngestTelemetry;

using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Serialization;

public sealed class TelemetryChannelBroker
{
    private readonly Channel<GlobalTelemetryPayload> _channel;

    public TelemetryChannelBroker(IOptions<TelemetryEngineOptions> options)
    {
        _channel = Channel.CreateBounded<GlobalTelemetryPayload>(
            new BoundedChannelOptions(options.Value.MemoryChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
    }

    public async ValueTask PublishAsync(GlobalTelemetryPayload analyticsEvent, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(analyticsEvent, cancellationToken);
        TelemetryDiagnostics.IngestedEventsTotal.Add(1);
        TelemetryDiagnostics.IncrementQueueCounter();
    }

    public ChannelReader<GlobalTelemetryPayload> Reader => _channel.Reader;
}

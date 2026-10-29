namespace Nealytics.Engine.Infrastructure.Storage.Delivery;

using System;
using Nealytics.Engine.Infrastructure.Serialization;

public sealed class TelemetryDeliveryUnit
{
    public int Version { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceId { get; init; }
    public required string Schema { get; init; }
    public DateTimeOffset IngestedAt { get; init; } = DateTimeOffset.UtcNow;
    public required GlobalTelemetryPayload[] Items { get; init; }
}

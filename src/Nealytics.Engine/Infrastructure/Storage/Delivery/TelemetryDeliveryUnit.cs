namespace Nealytics.Engine.Infrastructure.Storage.Delivery;

using System;
using Nealytics.Engine.Infrastructure.Serialization;

public sealed class TelemetryDeliveryUnit
{
    public int Version { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceId { get; init; }
    public string Schema { get; init; } = string.Empty;
    public DateTimeOffset IngestedAt { get; init; } = DateTimeOffset.UtcNow;
    public GlobalTelemetryPayload[] Items { get; init; } = [];
}

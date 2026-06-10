namespace Nealytics.Engine.Infrastructure.Serialization;

using System;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using Nealytics.Engine.Features.GetProjectTimeline;
using Nealytics.Engine.Features.GetSessionAnalytics;

public sealed class GlobalTelemetryPayload
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public string ProjectId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string? ItemId { get; init; }
    public string MetadataJson { get; set; } = "{}";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GlobalTelemetryPayload))]
[JsonSerializable(typeof(List<GlobalTelemetryPayload>))]
[JsonSerializable(typeof(ProjectTimelineResponse))]
[JsonSerializable(typeof(GlobalTimelineItem))]
[JsonSerializable(typeof(List<GlobalTimelineItem>))]
[JsonSerializable(typeof(SessionAnalyticsResponse))]
[JsonSerializable(typeof(SessionSummaryItem))]
[JsonSerializable(typeof(List<SessionSummaryItem>))]
public partial class TelemetryAotContext : JsonSerializerContext
{
}

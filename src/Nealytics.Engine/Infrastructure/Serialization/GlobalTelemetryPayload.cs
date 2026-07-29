namespace Nealytics.Engine.Infrastructure.Serialization;

using System;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using Nealytics.Engine.Features.GetProjectTimeline;
using Nealytics.Engine.Features.GetSessionAnalytics;
using Nealytics.Engine.Features.GetEventTimeSeries;
using Nealytics.Engine.Features.GetActiveUsers;
using Nealytics.Engine.Features.GetTopEvents;

public sealed class GlobalTelemetryPayload
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public string ProjectId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string? UserId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string? ItemId { get; init; }

    // Dimensions that used to be buried in MetadataJson. Aggregates cannot group on a JSON
    // string without parsing every row, which is why per-menu and per-section analytics
    // reported themselves unmeasured while the data was arriving all along.
    public string? MenuId { get; init; }
    public string? SectionId { get; init; }

    /// <summary>
    /// The table a diner scanned, resolved at the edge from the QR code's tracking token.
    /// Null for menu-scoped codes and for direct visits.
    /// </summary>
    public string? TableId { get; init; }

    // Derived at the edge from the User-Agent and Cloudflare request metadata, never sent by
    // the client — so a caller cannot forge them, and they cost the menu document nothing.
    public string DeviceClass { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public string Browser { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;

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
[JsonSerializable(typeof(EventTimeSeriesResponse))]
[JsonSerializable(typeof(EventTimeSeriesPoint))]
[JsonSerializable(typeof(List<EventTimeSeriesPoint>))]
[JsonSerializable(typeof(ActiveUsersResponse))]
[JsonSerializable(typeof(ActiveUsersPoint))]
[JsonSerializable(typeof(List<ActiveUsersPoint>))]
[JsonSerializable(typeof(TopEventsResponse))]
[JsonSerializable(typeof(TopEventItem))]
[JsonSerializable(typeof(List<TopEventItem>))]
public partial class TelemetryAotContext : JsonSerializerContext
{
}

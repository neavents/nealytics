namespace Nealytics.Engine.Infrastructure.Serialization;

using System;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using Nealytics.Engine.Features.GetProjectTimeline;
using Nealytics.Engine.Features.GetSessionAnalytics;
using Nealytics.Engine.Features.GetEventTimeSeries;
using Nealytics.Engine.Features.GetActiveUsers;
using Nealytics.Engine.Features.GetTopEvents;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetDistribution;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Features.GetFunnel;
using Nealytics.Engine.Features.GetSchema;
using Nealytics.Engine.Features.ValidateTelemetry;

public sealed class GlobalTelemetryPayload
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public string ProjectId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string? UserId { get; init; }
    public string EventType { get; init; } = string.Empty;

    /// <summary>The thing this event is about. Generic by design — every engine has one.</summary>
    public string? ObjectId { get; init; }

    /// <summary>
    /// Deployment-declared dimensions, keyed by column name.
    ///
    /// These are real typed columns, not a JSON bag: ClickHouse cannot GROUP BY a field inside a
    /// JSON string without parsing every row, which is why per-dimension analytics used to report
    /// itself unmeasured while the data was arriving all along. What changed is only *where the
    /// names come from* — configuration, never this assembly. A key that is not declared is
    /// dropped at ingest, loudly and counted; see IngestValidation.SanitizeDimensions.
    /// </summary>
    public Dictionary<string, string>? Dimensions { get; init; }

    public Dictionary<string, string>? Measures { get; init; }

    /// <summary>
    /// Monotonic per session, starting at 0. The ordering key within a session.
    ///
    /// Client clocks are unreliable — badly so on cheap devices — so anything that orders events
    /// inside a session must order by this, never by <see cref="Timestamp"/>.
    /// </summary>
    public uint Seq { get; init; }

    /// <summary>
    /// <c>normal</c>, <c>bot</c> or <c>internal</c>. Read endpoints exclude everything but
    /// <c>normal</c> unless asked otherwise.
    ///
    /// Flagged, never dropped: when an owner asks why a number differs from their own count, the
    /// raw rows are the only way to answer.
    /// </summary>
    public string TrafficClass { get; init; } = string.Empty;

    public string PagePath { get; init; } = string.Empty;

    public string Referrer { get; init; } = string.Empty;

    // Derived at the edge from the User-Agent and Cloudflare request metadata, never sent by
    // the client — so a caller cannot forge them, and they cost the source document nothing.
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
[JsonSerializable(typeof(Dictionary<string, string>))]
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
[JsonSerializable(typeof(BreakdownResponse))]
[JsonSerializable(typeof(BreakdownRow))]
[JsonSerializable(typeof(List<BreakdownRow>))]
[JsonSerializable(typeof(SchemaResponse))]
[JsonSerializable(typeof(SchemaDimension))]
[JsonSerializable(typeof(SchemaMeasure))]
[JsonSerializable(typeof(List<SchemaDimension>))]
[JsonSerializable(typeof(List<SchemaMeasure>))]
[JsonSerializable(typeof(SchemaEventType))]
[JsonSerializable(typeof(List<SchemaEventType>))]
[JsonSerializable(typeof(FunnelResponse))]
[JsonSerializable(typeof(FunnelStepResult))]
[JsonSerializable(typeof(FunnelSegment))]
[JsonSerializable(typeof(List<FunnelStepResult>))]
[JsonSerializable(typeof(List<FunnelSegment>))]
[JsonSerializable(typeof(ValidateTelemetryResponse))]
[JsonSerializable(typeof(PivotResponse))]
[JsonSerializable(typeof(PivotRow))]
[JsonSerializable(typeof(List<PivotRow>))]
[JsonSerializable(typeof(PivotMetricDescriptor))]
[JsonSerializable(typeof(List<PivotMetricDescriptor>))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(DistributionResponse))]
[JsonSerializable(typeof(DistributionQuantile))]
[JsonSerializable(typeof(List<DistributionQuantile>))]
[JsonSerializable(typeof(DistributionBucket))]
[JsonSerializable(typeof(List<DistributionBucket>))]
public partial class TelemetryAotContext : JsonSerializerContext
{
}

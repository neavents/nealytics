namespace Nealytics.Engine.Features.GetProjectTimeline;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

public sealed class GlobalTimelineItem
{
    public Dictionary<string, string>? Dimensions { get; set; }

    public Dictionary<string, string>? Measures { get; set; }

    public Guid EventId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? ObjectId { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTime Timestamp { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; set; }
}

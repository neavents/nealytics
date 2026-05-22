namespace Nealytics.Engine.Features.GetProjectTimeline;

using System;

public sealed class GlobalTimelineItem
{
    public Guid EventId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? ItemId { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTime Timestamp { get; set; }
}

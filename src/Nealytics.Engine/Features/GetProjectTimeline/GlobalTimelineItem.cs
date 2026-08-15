namespace Nealytics.Engine.Features.GetProjectTimeline;

using System;
using System.Collections.Generic;

public sealed class GlobalTimelineItem
{
    /// <summary>
    /// Declared dimension and measure values for this one event, keyed by column name.
    ///
    /// A serialised map rather than fixed properties because the engine does not know the
    /// deployment's column names — the same reason they are declared in the first place. This is a
    /// response shape, not a storage shape: the values are read out of typed columns.
    /// </summary>
    public Dictionary<string, string>? Dimensions { get; set; }

    public Dictionary<string, string>? Measures { get; set; }

    public Guid EventId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? ObjectId { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTime Timestamp { get; set; }
}

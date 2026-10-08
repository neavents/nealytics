namespace Nealytics.Engine.Features.GetProjectTimeline;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

public sealed class ProjectTimelineResponse
{
    public string ProjectId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantSet { get; init; }
    public IReadOnlyList<GlobalTimelineItem> Events { get; init; } = Array.Empty<GlobalTimelineItem>();
}

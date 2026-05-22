namespace Nealytics.Engine.Features.GetProjectTimeline;

using System;
using System.Collections.Generic;

public sealed class ProjectTimelineResponse
{
    public string ProjectId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public IReadOnlyList<GlobalTimelineItem> Events { get; init; } = Array.Empty<GlobalTimelineItem>();
}

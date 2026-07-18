namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;
using System.Collections.Generic;

public sealed class EventTimeSeriesResponse
{
    public string ProjectId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string Interval { get; init; } = string.Empty;
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public long TotalCount { get; init; }
    public IReadOnlyList<EventTimeSeriesPoint> Points { get; init; } = Array.Empty<EventTimeSeriesPoint>();
}

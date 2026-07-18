namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;

public readonly struct EventTimeSeriesRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public TimeSeriesInterval Interval { get; init; }
    public string? EventType { get; init; }
    public int Limit { get; init; }
}

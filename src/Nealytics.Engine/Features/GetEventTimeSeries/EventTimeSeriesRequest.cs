namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Query;

public readonly struct EventTimeSeriesRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public TimeSeriesInterval Interval { get; init; }
    public string? EventType { get; init; }
    public string? GroupByColumn { get; init; }
    public string? TimeZone { get; init; }
    public string? TrafficClass { get; init; }

    public IReadOnlyList<QueryFilter> Filters { get; init; }

    public int Limit { get; init; }

    public QueryScope Scope => new()
    {
        ProjectId = ProjectId,
        TenantId = TenantId,
        From = From,
        To = To,
        TrafficClass = TrafficClass,
        EventType = EventType,
        Filters = Filters,
    };
}

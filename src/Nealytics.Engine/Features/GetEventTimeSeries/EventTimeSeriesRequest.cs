namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Features.GetBreakdown;

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

    /// <summary>
    /// Resolved column filters, so a series can be scoped to one menu, section or item.
    ///
    /// Without these a time series could only ever be asked of a whole tenant, so "scans over time
    /// for THIS menu" was not expressible — every per-entity chart in a dashboard either showed the
    /// venue's total under a menu's heading or could not be built at all. Same parser, same
    /// allowlist and same injection boundary as /breakdown: the column is the allowlist's own
    /// instance and the value is bound.
    /// </summary>
    public IReadOnlyList<BreakdownFilter> Filters { get; init; }

    public int Limit { get; init; }
}

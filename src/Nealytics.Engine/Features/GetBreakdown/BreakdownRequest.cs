namespace Nealytics.Engine.Features.GetBreakdown;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Query;

public enum BreakdownMetric
{
    Events,

    Sessions,

    Users,

    Measure,
}

public enum BreakdownOrder
{
    ValueDescending,

    ValueAscending,

    KeyAscending,
}

public readonly struct BreakdownRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public BreakdownMetric Metric { get; init; }

    public string? MeasureColumn { get; init; }

    public string? MeasureFunction { get; init; }

    public string MetricWire { get; init; }

    public string? TrafficClass { get; init; }

    public bool Exact { get; init; }
    public string GroupByColumn { get; init; }
    public string? EventType { get; init; }
    public IReadOnlyList<QueryFilter> Filters { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int Limit { get; init; }
    public BreakdownOrder Order { get; init; }

    public bool Approximate { get; init; }

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

public sealed class BreakdownRow
{
    public string Key { get; set; } = string.Empty;

    public double Value { get; set; }

    public double Share { get; set; }
}

public sealed class BreakdownResponse
{
    public string GroupBy { get; init; } = string.Empty;
    public string Metric { get; init; } = string.Empty;
    public DateTime From { get; init; }
    public DateTime To { get; init; }

    public string Grain { get; init; } = "event";

    public string Source { get; init; } = "raw";


    public double Total { get; init; }

    public long GroupCount { get; init; }

    public bool Truncated { get; init; }

    public IReadOnlyList<BreakdownRow> Rows { get; init; } = Array.Empty<BreakdownRow>();
}

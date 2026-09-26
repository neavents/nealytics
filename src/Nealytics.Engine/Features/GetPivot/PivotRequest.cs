namespace Nealytics.Engine.Features.GetPivot;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Query;

public enum PivotMetricKind
{
    Events,
    Sessions,
    Users,
    Distinct,
    Measure,
}

public readonly struct PivotMetric
{
    public PivotMetricKind Kind { get; init; }
    public string Wire { get; init; }
    public string? EventType { get; init; }
    public string? Column { get; init; }
    public string? AggregationName { get; init; }
    public string? AggregationParameters { get; init; }
    public string? CanonicalAggregation { get; init; }

    public string Grain => Kind switch
    {
        PivotMetricKind.Sessions => "session",
        PivotMetricKind.Users => "user",
        _ => "event",
    };
}

public readonly struct PivotRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public string GroupByColumn { get; init; }
    public IReadOnlyList<PivotMetric> Metrics { get; init; }
    public IReadOnlyList<QueryFilter> Filters { get; init; }
    public string? TrafficClass { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int Limit { get; init; }
    public int OrderByMetric { get; init; }
    public bool OrderByKey { get; init; }
    public bool Descending { get; init; }
    public bool Approximate { get; init; }
    public bool Exact { get; init; }
    public bool EmptyAsNull { get; init; }

    public QueryScope Scope => new()
    {
        ProjectId = ProjectId,
        TenantId = TenantId,
        From = From,
        To = To,
        TrafficClass = TrafficClass,
        EventType = null,
        Filters = Filters,
    };
}

public sealed class PivotMetricDescriptor
{
    public string Spec { get; init; } = string.Empty;
    public string Grain { get; init; } = "event";
    public string? EventType { get; init; }
}

public sealed class PivotRow
{
    public string Key { get; set; } = string.Empty;
    public double?[] Values { get; set; } = [];
}

public sealed class PivotResponse
{
    public string GroupBy { get; init; } = string.Empty;
    public IReadOnlyList<PivotMetricDescriptor> Metrics { get; init; } = Array.Empty<PivotMetricDescriptor>();
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string Source { get; init; } = "raw";
    public double?[] Totals { get; init; } = [];
    public long GroupCount { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<PivotRow> Rows { get; init; } = Array.Empty<PivotRow>();
}

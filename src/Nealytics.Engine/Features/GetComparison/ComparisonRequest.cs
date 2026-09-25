namespace Nealytics.Engine.Features.GetComparison;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Infrastructure.Query;

public enum ComparisonOrder
{
    Current,
    Previous,
    Change,
    Key,
}

public readonly struct ComparisonRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public PivotMetric Metric { get; init; }
    public string? GroupByColumn { get; init; }
    public IReadOnlyList<QueryFilter> Filters { get; init; }
    public string? TrafficClass { get; init; }
    public string? TimeZone { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public DateTime PreviousFrom { get; init; }
    public DateTime PreviousTo { get; init; }
    public int Limit { get; init; }
    public ComparisonOrder Order { get; init; }
    public bool Descending { get; init; }
    public bool Approximate { get; init; }
    public bool Exact { get; init; }

    public QueryScope Scope => new()
    {
        ProjectId = ProjectId,
        TenantId = TenantId,
        From = From,
        To = To,
        TrafficClass = TrafficClass,
        EventType = Metric.EventType,
        Filters = Filters,
    };
}

public sealed class ComparisonWindow
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
}

public sealed class ComparisonValue
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Current { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Previous { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Change { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? RelativeChange { get; init; }

    public static ComparisonValue Of(double current, double previous)
    {
        double? now = double.IsFinite(current) ? current : null;
        double? before = double.IsFinite(previous) ? previous : null;
        double? change = now is double a && before is double b ? a - b : null;
        double? relative = change is double delta && before is double baseline && baseline != 0 ? delta / baseline : null;
        return new ComparisonValue { Current = now, Previous = before, Change = change, RelativeChange = relative };
    }
}

public sealed class ComparisonRow
{
    public string Key { get; init; } = string.Empty;
    public ComparisonValue Value { get; init; } = new();
}

public sealed class ComparisonResponse
{
    public string Metric { get; init; } = string.Empty;
    public string Grain { get; init; } = "event";
    public string? EventType { get; init; }
    public string? GroupBy { get; init; }
    public string? TimeZone { get; init; }
    public ComparisonWindow Current { get; init; } = new();
    public ComparisonWindow Previous { get; init; } = new();
    public string Source { get; init; } = "raw";
    public ComparisonValue Totals { get; init; } = new();
    public long GroupCount { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<ComparisonRow> Rows { get; init; } = Array.Empty<ComparisonRow>();
}

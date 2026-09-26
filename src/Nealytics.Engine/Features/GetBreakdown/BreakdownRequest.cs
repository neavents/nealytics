namespace Nealytics.Engine.Features.GetBreakdown;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nealytics.Engine.Infrastructure.Query;

/// <summary>What is being counted.</summary>
public enum BreakdownMetric
{
    /// <summary>Rows. <c>count()</c>.</summary>
    Events,

    /// <summary>Distinct sessions — "how many visits", not "how many taps".</summary>
    Sessions,

    /// <summary>Distinct known users. Anonymous rows carry a NULL user_id and do not count.</summary>
    Users,

    /// <summary>A declared measure, aggregated by a function that measure declares.</summary>
    Measure,
}

public enum BreakdownOrder
{
    /// <summary>Biggest first. What a leaderboard wants.</summary>
    ValueDescending,

    ValueAscending,

    /// <summary>By key, so a chart's categories keep a stable order between calls.</summary>
    KeyAscending,
}

public readonly struct BreakdownRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public BreakdownMetric Metric { get; init; }

    /// <summary>Canonical measure column, resolved from the registry. Never caller input.</summary>
    public string? MeasureColumn { get; init; }

    /// <summary>Canonical ClickHouse aggregate, resolved from a closed map. Never caller input.</summary>
    public string? MeasureFunction { get; init; }

    /// <summary>How the metric is echoed back — <c>events</c>, or <c>avg(dwell_ms)</c>.</summary>
    public string MetricWire { get; init; }

    public string? TrafficClass { get; init; }

    /// <summary>
    /// Counts distinct event ids instead of rows.
    ///
    /// The table is a ReplacingMergeTree and no query uses FINAL, so a WAL replay after a restart
    /// can leave the same event present twice until a background merge collapses it. Slower and
    /// exact, versus fast and eventually right — opt in, and say which you asked for.
    /// </summary>
    public bool Exact { get; init; }
    public string GroupByColumn { get; init; }
    public string? EventType { get; init; }
    public IReadOnlyList<QueryFilter> Filters { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int Limit { get; init; }
    public BreakdownOrder Order { get; init; }

    /// <summary>
    /// Count distinct sessions and users approximately, with HyperLogLog, instead of exactly.
    ///
    /// <c>uniqExact</c> builds a real hash set of every distinct value in memory. That is the right
    /// default at the scale of one venue, and it is the thing that falls over first at the scale of
    /// a large estate: session ids are unique per visit, so the set grows with traffic and a wide
    /// range eventually meets ClickHouse's memory limit and fails the whole query rather than
    /// degrading. <c>uniq</c> is HyperLogLog — a fixed, small amount of memory regardless of
    /// cardinality, for roughly 1.6% error.
    ///
    /// Opt-in, so no existing number changes. <c>/active</c> has had the same switch since it was
    /// written; this brings the two endpoints into line rather than inventing a concept.
    /// </summary>
    public bool Approximate { get; init; }

    public bool EmptyAsNull { get; init; }

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
    /// <summary>The dimension value. Empty string when the column was NULL for those rows.</summary>
    public string Key { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Value { get; set; } = 0;

    /// <summary>
    /// This row's share of <see cref="BreakdownResponse.Total"/>, 0..1.
    ///
    /// Computed against the total across ALL groups, not the sum of the returned rows, so a capped
    /// response's shares still add up to less than one — which is the honest reading.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Share { get; set; } = 0;
}

public sealed class BreakdownResponse
{
    public string GroupBy { get; init; } = string.Empty;
    public string Metric { get; init; } = string.Empty;
    public DateTime From { get; init; }
    public DateTime To { get; init; }

    /// <summary>
    /// The grain every number in this response is counted at. Part of the metric's definition, not
    /// an implementation detail: two widgets that both say "visits" while counting at different
    /// grains generate support tickets forever.
    /// </summary>
    public string Grain { get; init; } = "event";

    /// <summary>
    /// Which store answered this — <c>raw</c>, or <c>rollup:&lt;name&gt;</c>.
    ///
    /// Reported rather than hidden, for the same reason <see cref="Truncated"/> is: a chart that
    /// silently changed data source is one nobody can debug, and "the rollup only covers part of
    /// this range" is exactly the kind of difference that otherwise reads as a traffic change.
    /// </summary>
    public string Source { get; init; } = "raw";


    /// <summary>The metric across every group in range, including groups not returned.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Total { get; init; } = 0;

    /// <summary>How many distinct groups exist in range, whether or not they were returned.</summary>
    public long GroupCount { get; init; }

    /// <summary>
    /// True when <see cref="GroupCount"/> exceeds the rows returned.
    ///
    /// Reported rather than silent. The dashboard already has a ROW_CAP that quietly flips coverage
    /// flags off; a second silent cap would mean a chart that is simply wrong with no way to tell.
    /// </summary>
    public bool Truncated { get; init; }

    public IReadOnlyList<BreakdownRow> Rows { get; init; } = Array.Empty<BreakdownRow>();
}

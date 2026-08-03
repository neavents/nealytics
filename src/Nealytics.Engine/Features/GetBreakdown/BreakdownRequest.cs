namespace Nealytics.Engine.Features.GetBreakdown;

using System;
using System.Collections.Generic;

/// <summary>What is being counted.</summary>
public enum BreakdownMetric
{
    /// <summary>Rows. <c>count()</c>.</summary>
    Events,

    /// <summary>Distinct sessions — "how many visits", not "how many taps".</summary>
    Sessions,

    /// <summary>Distinct known users. Anonymous rows carry a NULL user_id and do not count.</summary>
    Users,
}

public enum BreakdownOrder
{
    /// <summary>Biggest first. What a leaderboard wants.</summary>
    ValueDescending,

    ValueAscending,

    /// <summary>By key, so a chart's categories keep a stable order between calls.</summary>
    KeyAscending,
}

/// <summary>One validated <c>name = value</c> filter. The name is a canonical column; the value is parameterised.</summary>
public readonly struct BreakdownFilter
{
    public string Column { get; init; }
    public string Value { get; init; }
}

public readonly struct BreakdownRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public BreakdownMetric Metric { get; init; }
    public string GroupByColumn { get; init; }
    public string? EventType { get; init; }
    public IReadOnlyList<BreakdownFilter> Filters { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int Limit { get; init; }
    public BreakdownOrder Order { get; init; }
}

public sealed class BreakdownRow
{
    /// <summary>The dimension value. Empty string when the column was NULL for those rows.</summary>
    public string Key { get; set; } = string.Empty;

    public long Value { get; set; }

    /// <summary>
    /// This row's share of <see cref="BreakdownResponse.Total"/>, 0..1.
    ///
    /// Computed against the total across ALL groups, not the sum of the returned rows, so a capped
    /// response's shares still add up to less than one — which is the honest reading.
    /// </summary>
    public double Share { get; set; }
}

public sealed class BreakdownResponse
{
    public string GroupBy { get; init; } = string.Empty;
    public string Metric { get; init; } = string.Empty;
    public DateTime From { get; init; }
    public DateTime To { get; init; }

    /// <summary>The metric across every group in range, including groups not returned.</summary>
    public long Total { get; init; }

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

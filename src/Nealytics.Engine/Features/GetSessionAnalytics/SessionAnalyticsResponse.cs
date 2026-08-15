namespace Nealytics.Engine.Features.GetSessionAnalytics;

using System;
using System.Collections.Generic;

public sealed class SessionAnalyticsResponse
{
    public string ProjectId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public long UniqueSessionCount { get; init; }
    public long TotalEventCount { get; init; }
    public double AvgDurationSeconds { get; init; }

    /// <summary>
    /// True when more sessions exist in range than were returned.
    ///
    /// The counts above are range-scoped, so they stay right when this is true — only the
    /// <see cref="Sessions"/> list is a page.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Which store answered: <c>raw</c> or <c>rollup:{name}</c>.
    ///
    /// The same honesty property <c>truncated</c> has, applied to performance. Both paths return
    /// the same numbers by construction, so nothing else in the response would reveal that a chart
    /// silently changed its data source — and a chart that did is one nobody can debug.
    /// </summary>
    public string Source { get; init; } = "raw";
    public IReadOnlyList<SessionSummaryItem> Sessions { get; init; } = Array.Empty<SessionSummaryItem>();
}

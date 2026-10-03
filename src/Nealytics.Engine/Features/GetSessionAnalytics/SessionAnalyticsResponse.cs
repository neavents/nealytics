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

    public bool Truncated { get; init; }

    public string Source { get; init; } = "raw";
    public IReadOnlyList<SessionSummaryItem> Sessions { get; init; } = Array.Empty<SessionSummaryItem>();
}

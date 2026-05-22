namespace Nealytics.Engine.Features.GetSessionAnalytics;

using System;
using System.Collections.Generic;

public sealed class SessionAnalyticsResponse
{
    public string ProjectId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public int UniqueSessionCount { get; init; }
    public long TotalEventCount { get; init; }
    public double AvgDurationSeconds { get; init; }
    public IReadOnlyList<SessionSummaryItem> Sessions { get; init; } = Array.Empty<SessionSummaryItem>();
}

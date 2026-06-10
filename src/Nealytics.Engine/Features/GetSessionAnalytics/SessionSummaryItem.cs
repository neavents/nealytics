namespace Nealytics.Engine.Features.GetSessionAnalytics;

using System;

public sealed class SessionSummaryItem
{
    public string SessionId { get; set; } = string.Empty;
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public double DurationSeconds { get; set; }
    public long EventCount { get; set; }
}

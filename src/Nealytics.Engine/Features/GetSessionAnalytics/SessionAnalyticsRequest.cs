namespace Nealytics.Engine.Features.GetSessionAnalytics;

using System;

public readonly struct SessionAnalyticsRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int Limit { get; init; }
}

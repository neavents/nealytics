namespace Nealytics.Engine.Features.GetTopEvents;

using System;
using Nealytics.Engine.Infrastructure.Query;

public readonly struct TopEventsRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public TenantSet? TenantSet { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string DimensionColumn { get; init; }
    public string? TrafficClass { get; init; }
    public int Limit { get; init; }

    public bool Exact { get; init; }
}

namespace Nealytics.Engine.Features.GetTopEvents;

using System;

public readonly struct TopEventsRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public TopDimension Dimension { get; init; }
    public int Limit { get; init; }
}

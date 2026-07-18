namespace Nealytics.Engine.Features.GetProjectTimeline;

using System;

public readonly struct TimelineQueryRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public int Limit { get; init; }
    public DateTime? Before { get; init; }
    public string? EventType { get; init; }
    public string? SessionId { get; init; }
    public string? ItemId { get; init; }
}

namespace Nealytics.Engine.Features.GetTopEvents;

using System;
using System.Collections.Generic;

public sealed class TopEventsResponse
{
    public string ProjectId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string Dimension { get; init; } = string.Empty;
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public IReadOnlyList<TopEventItem> Items { get; init; } = Array.Empty<TopEventItem>();
}

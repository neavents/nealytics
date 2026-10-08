namespace Nealytics.Engine.Features.GetActiveUsers;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Query;

public readonly struct ActiveUsersRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public TenantSet? TenantSet { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public ActiveUsersInterval Interval { get; init; }
    public ActiveDimension Dimension { get; init; }
    public ActiveCountMode Mode { get; init; }
    public string? TimeZone { get; init; }
    public string? TrafficClass { get; init; }
    public string? EventType { get; init; }
    public IReadOnlyList<QueryFilter> Filters { get; init; }
    public int Limit { get; init; }

    public QueryScope Scope => new()
    {
        ProjectId = ProjectId,
        TenantId = TenantId,
        TenantSet = TenantSet,
        From = From,
        To = To,
        TrafficClass = TrafficClass,
        EventType = EventType,
        Filters = Filters,
    };
}

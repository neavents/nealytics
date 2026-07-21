namespace Nealytics.Engine.Features.GetActiveUsers;

using System;

public readonly struct ActiveUsersRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public ActiveUsersInterval Interval { get; init; }
    public ActiveDimension Dimension { get; init; }
    public ActiveCountMode Mode { get; init; }
    public int Limit { get; init; }
}

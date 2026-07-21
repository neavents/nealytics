namespace Nealytics.Engine.Features.GetActiveUsers;

using System;
using System.Collections.Generic;

public sealed class ActiveUsersResponse
{
    public string ProjectId { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string Interval { get; init; } = string.Empty;
    public string By { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public IReadOnlyList<ActiveUsersPoint> Points { get; init; } = Array.Empty<ActiveUsersPoint>();
}

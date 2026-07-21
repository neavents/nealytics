namespace Nealytics.Engine.Features.GetActiveUsers;

using System;

public sealed class ActiveUsersPoint
{
    public DateTime Bucket { get; set; }
    public long ActiveCount { get; set; }
}

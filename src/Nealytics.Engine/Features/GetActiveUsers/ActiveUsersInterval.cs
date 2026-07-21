namespace Nealytics.Engine.Features.GetActiveUsers;

using System;

public enum ActiveUsersInterval
{
    Day,
    Month
}

public static class ActiveUsersIntervalParser
{
    public static bool TryParse(string? value, out ActiveUsersInterval interval)
    {
        switch (value)
        {
            case "day":
                interval = ActiveUsersInterval.Day;
                return true;
            case "month":
                interval = ActiveUsersInterval.Month;
                return true;
            default:
                interval = ActiveUsersInterval.Day;
                return false;
        }
    }

    public static string ToWireFormat(ActiveUsersInterval interval)
    {
        switch (interval)
        {
            case ActiveUsersInterval.Day:
                return "day";
            case ActiveUsersInterval.Month:
                return "month";
            default:
                throw new ArgumentOutOfRangeException(nameof(interval));
        }
    }

    public static string ToBucketFunction(ActiveUsersInterval interval)
    {
        switch (interval)
        {
            case ActiveUsersInterval.Day:
                return "toStartOfDay";
            case ActiveUsersInterval.Month:
                return "toStartOfMonth";
            default:
                throw new ArgumentOutOfRangeException(nameof(interval));
        }
    }
}

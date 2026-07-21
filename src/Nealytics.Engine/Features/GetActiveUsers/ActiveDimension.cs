namespace Nealytics.Engine.Features.GetActiveUsers;

using System;

public enum ActiveDimension
{
    User,
    Session
}

public static class ActiveDimensionParser
{
    public static bool TryParse(string? value, out ActiveDimension dimension)
    {
        switch (value)
        {
            case "user":
                dimension = ActiveDimension.User;
                return true;
            case "session":
                dimension = ActiveDimension.Session;
                return true;
            default:
                dimension = ActiveDimension.User;
                return false;
        }
    }

    public static string ToWireFormat(ActiveDimension dimension)
    {
        switch (dimension)
        {
            case ActiveDimension.User:
                return "user";
            case ActiveDimension.Session:
                return "session";
            default:
                throw new ArgumentOutOfRangeException(nameof(dimension));
        }
    }

    public static string ToColumn(ActiveDimension dimension)
    {
        switch (dimension)
        {
            case ActiveDimension.User:
                return "user_id";
            case ActiveDimension.Session:
                return "session_id";
            default:
                throw new ArgumentOutOfRangeException(nameof(dimension));
        }
    }
}

namespace Nealytics.Engine.Features.GetActiveUsers;

using System;

public enum ActiveCountMode
{
    Exact,
    Approx
}

public static class ActiveCountModeParser
{
    public static bool TryParse(string? value, out ActiveCountMode mode)
    {
        switch (value)
        {
            case "exact":
                mode = ActiveCountMode.Exact;
                return true;
            case "approx":
                mode = ActiveCountMode.Approx;
                return true;
            default:
                mode = ActiveCountMode.Exact;
                return false;
        }
    }

    public static string ToWireFormat(ActiveCountMode mode)
    {
        switch (mode)
        {
            case ActiveCountMode.Exact:
                return "exact";
            case ActiveCountMode.Approx:
                return "approx";
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    public static string ToUniqFunction(ActiveCountMode mode)
    {
        switch (mode)
        {
            case ActiveCountMode.Exact:
                return "uniqExact";
            case ActiveCountMode.Approx:
                return "uniq";
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }
}

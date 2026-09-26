namespace Nealytics.Engine.Infrastructure.Query;

using System;

public static class EmptyCells
{
    public const string Zero = "zero";
    public const string Null = "null";

    public static bool TryParse(string? requested, out bool asNull)
    {
        asNull = string.Equals(requested, Null, StringComparison.Ordinal);
        return asNull || string.IsNullOrEmpty(requested) || string.Equals(requested, Zero, StringComparison.Ordinal);
    }

    public static string Rejection(string? requested) =>
        $"'empty' must be zero or null. Got '{requested}'.";
}

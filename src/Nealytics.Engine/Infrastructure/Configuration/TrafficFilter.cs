namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Generic;

public static class TrafficFilter
{
    public const string Normal = "normal";
    public const string All = "all";

    private static readonly HashSet<string> Known =
        new(StringComparer.Ordinal) { Normal, "bot", "internal" };

    public static bool TryParse(string? requested, out string? trafficClass)
    {
        if (string.IsNullOrEmpty(requested))
        {
            trafficClass = Normal;
            return true;
        }

        if (string.Equals(requested, All, StringComparison.Ordinal))
        {
            trafficClass = null;
            return true;
        }

        if (Known.TryGetValue(requested, out string? canonical))
        {
            trafficClass = canonical;
            return true;
        }

        trafficClass = Normal;
        return false;
    }

    public static string Rejection(string? requested) =>
        $"'traffic' must be one of: normal, bot, internal, all. Got '{requested}'.";

    public static string Clause(string? trafficClass) =>
        trafficClass is null ? string.Empty : " AND traffic_class = {trafficClass:String}";
}

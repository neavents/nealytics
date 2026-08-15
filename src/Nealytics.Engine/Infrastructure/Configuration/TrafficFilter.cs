namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Generic;

/// <summary>
/// Which traffic a read counts.
///
/// Bots were labelled at the edge from the day <c>device_class</c> existed and nothing ever filtered
/// on the label, so every number the dashboard has shown includes preview crawlers and link
/// unfurlers. Excluding them by default is the fix; the numbers dropping when this ships is the fix
/// working, not a regression.
///
/// The escape hatch matters as much as the default. When an owner says a figure disagrees with
/// their own count, the only way to answer is to look at what was excluded — so nothing is dropped
/// at ingest and <c>traffic=all</c> always gets it back.
/// </summary>
public static class TrafficFilter
{
    public const string Normal = "normal";
    public const string All = "all";

    private static readonly HashSet<string> Known =
        new(StringComparer.Ordinal) { Normal, "bot", "internal" };

    /// <summary>
    /// Resolves the query parameter. <paramref name="trafficClass"/> is null for "count everything".
    /// </summary>
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
            // The stored instance, never the caller's string — the same property the column
            // allowlist relies on, even though this one reaches SQL as a bound parameter.
            trafficClass = canonical;
            return true;
        }

        trafficClass = Normal;
        return false;
    }

    public static string Rejection(string? requested) =>
        $"'traffic' must be one of: normal, bot, internal, all. Got '{requested}'.";

    /// <summary>The WHERE fragment, or empty when every class counts.</summary>
    public static string Clause(string? trafficClass) =>
        trafficClass is null ? string.Empty : " AND traffic_class = {trafficClass:String}";
}

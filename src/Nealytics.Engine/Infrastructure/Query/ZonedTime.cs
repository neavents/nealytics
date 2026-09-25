namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Globalization;
using Nealytics.Engine.Infrastructure.Configuration;

public static class ZonedTime
{
    private const int MaxGapSteps = 8;
    private static readonly TimeSpan GapStep = TimeSpan.FromMinutes(30);

    public static bool TryResolve(string? name, out TimeZoneInfo? zone)
    {
        zone = null;

        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        return TimeBucket.IsWellFormed(name) && TimeZoneInfo.TryFindSystemTimeZoneById(name, out zone);
    }

    public static bool TryParseInstant(string raw, TimeZoneInfo? zone, out DateTime utc)
    {
        utc = default;

        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed))
        {
            return false;
        }

        utc = parsed.Kind switch
        {
            DateTimeKind.Utc => parsed,
            DateTimeKind.Local => parsed.ToUniversalTime(),
            _ => zone is null ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : ToUtc(parsed, zone),
        };
        return true;
    }

    public static DateTime ToUtc(DateTime wallClock, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTime local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

        for (int step = 0; step < MaxGapSteps && zone.IsInvalidTime(local); step++)
        {
            local = local.Add(GapStep);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static (DateTime From, DateTime To) Preceding(DateTime fromUtc, DateTime toUtc, TimeZoneInfo? zone)
    {
        if (zone is null)
        {
            return (fromUtc - (toUtc - fromUtc), fromUtc);
        }

        DateTime localFrom = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone);
        DateTime localTo = TimeZoneInfo.ConvertTimeFromUtc(toUtc, zone);
        return (ToUtc(localFrom - (localTo - localFrom), zone), fromUtc);
    }
}

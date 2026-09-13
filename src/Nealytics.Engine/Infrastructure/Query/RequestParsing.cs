namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Globalization;

public static class RequestParsing
{
    public const int MaxFieldLength = 256;

    public static bool TryParseRange(
        string? fromRaw, string? toRaw, int defaultRangeHours, DateTime nowUtc,
        out DateTime fromUtc, out DateTime toUtc)
    {
        fromUtc = nowUtc.AddHours(-defaultRangeHours);
        toUtc = nowUtc;

        if (!string.IsNullOrEmpty(fromRaw))
        {
            if (!TryParseUtc(fromRaw, out fromUtc))
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(toRaw))
        {
            if (!TryParseUtc(toRaw, out toUtc))
            {
                return false;
            }
        }

        return fromUtc <= toUtc;
    }

    public static bool TryParseUtc(string raw, out DateTime value) =>
        DateTime.TryParse(
            raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    public static int ParseLimit(string? limitRaw, int fallback, int maxLimit)
    {
        int limit = Math.Clamp(fallback, 1, maxLimit);

        if (!string.IsNullOrEmpty(limitRaw)
            && int.TryParse(limitRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            limit = Math.Clamp(parsed, 1, maxLimit);
        }

        return limit;
    }

    public static bool IsFlag(string? raw) => string.Equals(raw, "true", StringComparison.Ordinal);
}

namespace Nealytics.Engine.Infrastructure.Configuration;

using System;

public static class TimeBucket
{
    public const int MaxTimeZoneLength = 64;

    public static string Expression(string? timeZone) =>
        string.IsNullOrEmpty(timeZone) ? "(timestamp)" : "(toTimeZone(timestamp, {tz:String}))";

    public static bool IsWellFormed(string timeZone)
    {
        if (timeZone.Length is 0 or > MaxTimeZoneLength)
        {
            return false;
        }

        foreach (char character in timeZone)
        {
            bool allowed = char.IsAsciiLetterOrDigit(character)
                || character is '/' or '_' or '-' or '+';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}

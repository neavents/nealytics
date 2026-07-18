namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;

public enum TimeSeriesInterval
{
    Minute,
    Hour,
    Day
}

public static class TimeSeriesIntervalParser
{
    public static bool TryParse(string? value, out TimeSeriesInterval interval)
    {
        switch (value)
        {
            case "minute":
                interval = TimeSeriesInterval.Minute;
                return true;
            case "hour":
                interval = TimeSeriesInterval.Hour;
                return true;
            case "day":
                interval = TimeSeriesInterval.Day;
                return true;
            default:
                interval = TimeSeriesInterval.Hour;
                return false;
        }
    }

    public static string ToWireFormat(TimeSeriesInterval interval)
    {
        switch (interval)
        {
            case TimeSeriesInterval.Minute:
                return "minute";
            case TimeSeriesInterval.Hour:
                return "hour";
            case TimeSeriesInterval.Day:
                return "day";
            default:
                throw new ArgumentOutOfRangeException(nameof(interval));
        }
    }

    public static string ToBucketFunction(TimeSeriesInterval interval)
    {
        switch (interval)
        {
            case TimeSeriesInterval.Minute:
                return "toStartOfMinute";
            case TimeSeriesInterval.Hour:
                return "toStartOfHour";
            case TimeSeriesInterval.Day:
                return "toStartOfDay";
            default:
                throw new ArgumentOutOfRangeException(nameof(interval));
        }
    }
}

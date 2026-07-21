namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;

public enum TimeSeriesGroupBy
{
    None,
    EventType,
    ItemId,
    SessionId
}

public static class TimeSeriesGroupByParser
{
    public static bool TryParse(string? value, out TimeSeriesGroupBy groupBy)
    {
        switch (value)
        {
            case "event_type":
                groupBy = TimeSeriesGroupBy.EventType;
                return true;
            case "item_id":
                groupBy = TimeSeriesGroupBy.ItemId;
                return true;
            case "session_id":
                groupBy = TimeSeriesGroupBy.SessionId;
                return true;
            default:
                groupBy = TimeSeriesGroupBy.None;
                return false;
        }
    }

    public static string ToColumn(TimeSeriesGroupBy groupBy)
    {
        switch (groupBy)
        {
            case TimeSeriesGroupBy.EventType:
                return "event_type";
            case TimeSeriesGroupBy.ItemId:
                return "item_id";
            case TimeSeriesGroupBy.SessionId:
                return "session_id";
            default:
                throw new ArgumentOutOfRangeException(nameof(groupBy));
        }
    }
}

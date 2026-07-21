namespace Nealytics.Engine.Features.GetTopEvents;

using System;

public enum TopDimension
{
    EventType,
    ItemId
}

public static class TopDimensionParser
{
    public static bool TryParse(string? value, out TopDimension dimension)
    {
        switch (value)
        {
            case "event_type":
                dimension = TopDimension.EventType;
                return true;
            case "item_id":
                dimension = TopDimension.ItemId;
                return true;
            default:
                dimension = TopDimension.EventType;
                return false;
        }
    }

    public static string ToWireFormat(TopDimension dimension)
    {
        switch (dimension)
        {
            case TopDimension.EventType:
                return "event_type";
            case TopDimension.ItemId:
                return "item_id";
            default:
                throw new ArgumentOutOfRangeException(nameof(dimension));
        }
    }

    public static string ToColumn(TopDimension dimension)
    {
        switch (dimension)
        {
            case TopDimension.EventType:
                return "event_type";
            case TopDimension.ItemId:
                return "item_id";
            default:
                throw new ArgumentOutOfRangeException(nameof(dimension));
        }
    }

    public static bool ExcludesNull(TopDimension dimension) => dimension == TopDimension.ItemId;
}

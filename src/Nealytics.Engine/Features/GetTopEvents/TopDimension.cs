namespace Nealytics.Engine.Features.GetTopEvents;

public static class TopDimensionRules
{
    public static bool ExcludesNull(string column) =>
        !string.Equals(column, "event_type", System.StringComparison.Ordinal);
}

namespace Nealytics.Engine.Infrastructure.Query;

using System.Text;
using System.Linq;
using Nealytics.Engine.Infrastructure.Configuration;

internal static class ReportShapeSql
{
    internal static string? Bucket(string? interval) => interval switch
    {
        null => null,
        "hour" => "toStartOfHour(toTimeZone(timestamp, 'UTC'))",
        "day" => "toStartOfDay(toTimeZone(timestamp, 'UTC'))",
        _ => throw new System.InvalidOperationException("Unknown compiled report interval.")
    };

    internal static void AppendKeys(StringBuilder sql, SavedReport definition)
    {
        if (Bucket(definition.Interval) is string bucket)
        {
            sql.Append(bucket).Append(", ");
        }
        foreach (string column in definition.GroupBy)
        {
            sql.Append("toString(").Append(column).Append("), ");
        }
    }

    internal static void AppendGrouping(StringBuilder sql, SavedReport definition)
    {
        if (!definition.IsGrouped)
        {
            return;
        }
        int keys = definition.GroupBy.Count + (definition.Interval is null ? 0 : 1);
        sql.Append(" GROUP BY ");
        AppendOrdinals(sql, keys, ordering: false);
        sql.Append(" ORDER BY ");
        AppendOrdinals(sql, keys, ordering: true);
        sql.Append(" LIMIT ").Append(definition.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sql.Append(" SETTINGS enable_positional_arguments=1");
        if (definition.Interval is not null)
        {
            sql.Append(", enable_extended_results_for_datetime_functions=1");
        }
    }

    private static void AppendOrdinals(StringBuilder sql, int count, bool ordering)
    {
        foreach (int index in Enumerable.Range(1, count))
        {
            if (index > 1)
            {
                sql.Append(", ");
            }
            sql.Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (ordering)
            {
                sql.Append(" ASC NULLS FIRST");
            }
        }
    }
}

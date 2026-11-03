namespace Nealytics.Engine.Infrastructure.Query;

using System.Globalization;
using System.Text;
using Nealytics.Engine.Infrastructure.Configuration;

internal static class MetricAggregateSql
{
    private static readonly string[] QuantileLevels = ["0.5", "0.75", "0.9", "0.95", "0.99"];

    internal static void Append(StringBuilder sql, NamedMetric definition, int? eventTypeIndex = null)
    {
        sql.Append("toString(");
        string? column = definition.Measure;
        if (definition.Aggregation == "count" && eventTypeIndex is int countIndex)
        {
            sql.Append("countIf(");
            if (column is not null)
            {
                sql.Append("isNotNull(").Append(column).Append(") AND ");
            }
            AppendEventType(sql, countIndex);
            sql.Append("))");
            return;
        }

        string function = definition.Aggregation;
        if (function is "min" or "max" or "avg")
        {
            function += "OrNull";
        }
        if (eventTypeIndex is not null)
        {
            function += "If";
        }
        int? quantileIndex = RollupRegistry.QuantileIndex(definition.Aggregation);
        if (quantileIndex is not null)
        {
            string quantile = definition.Accuracy == "approximate" ? "quantileOrNull" : "quantileExactOrNull";
            function = quantile + (eventTypeIndex is null ? "" : "If")
                + "(" + QuantileLevels[quantileIndex.Value - 1] + ")";
        }
        if (definition.WidenSum)
        {
            column = "toInt256(" + column + ")";
        }
        if (definition.Aggregation == "sum")
        {
            sql.Append("coalesce(");
        }
        sql.Append(function).Append('(').Append(column);
        if (eventTypeIndex is int index)
        {
            sql.Append(", ");
            AppendEventType(sql, index);
        }
        sql.Append(')');
        if (definition.Aggregation == "sum")
        {
            sql.Append(", 0)");
        }
        sql.Append(')');
    }

    internal static string EventTypeParameter(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"reportEvent{index}");

    private static void AppendEventType(StringBuilder sql, int index) =>
        sql.Append("event_type = {").Append(EventTypeParameter(index)).Append(":String}");
}

namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Nealytics.Engine.Infrastructure.Configuration;

internal static class AnalyticsPlanSql
{
    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) Build(
        AnalyticsPlan plan, IReadOnlyList<NamedMetric> components, in QueryScope scope, SavedReport? report)
    {
        Rollup rollup = plan.Rollup ?? throw new InvalidOperationException("A state plan requires a rollup.");
        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(scope);
        parameters.RemoveAll(parameter => parameter.Key is "fromTimestamp" or "toTimestamp");
        for (int index = 0; index < components.Count; index++)
        {
            if (components[index].EventType is string eventType)
            {
                parameters.Add(new(MetricAggregateSql.EventTypeParameter(index), eventType));
            }
        }
        int keys = (report?.GroupBy.Count ?? 0) + (report?.Interval is null ? 0 : 1);
        StringBuilder sql = new("SELECT ");
        AppendAliases(sql, keys, trailing: true);
        for (int index = 0; index < components.Count; index++)
        {
            if (index != 0)
            {
                sql.Append(", ");
            }
            NamedMetric metric = components[index];
            sql.Append("toString(");
            if (metric.Aggregation == "sum")
            {
                sql.Append("coalesce(");
            }
            sql.Append(metric.Aggregation).Append("Merge(__c").Append(Index(index)).Append(')');
            if (metric.Aggregation == "sum")
            {
                sql.Append(", 0)");
            }
            sql.Append(')');
        }
        sql.Append(" FROM (");
        for (int sliceIndex = 0; sliceIndex < plan.Slices.Count; sliceIndex++)
        {
            AnalyticsPlanSlice slice = plan.Slices[sliceIndex];
            bool summarized = slice.Source == "rollup";
            if (sliceIndex != 0)
            {
                sql.Append(" UNION ALL ");
            }
            sql.Append("SELECT ");
            AppendKeys(sql, report, summarized);
            for (int index = 0; index < components.Count; index++)
            {
                if (index != 0)
                {
                    sql.Append(", ");
                }
                AppendState(sql, components[index], rollup, index, summarized);
            }
            sql.Append(" FROM nealytics_core.").Append(summarized ? rollup.TableName : RollupRegistry.SourceTable);
            StringBuilder predicate = new();
            if (summarized)
            {
                ScopeClause.AppendRollup(predicate, scope, "bucket", rollup);
            }
            else
            {
                ScopeClause.AppendRaw(predicate, scope, exclusiveEnd: true);
            }
            string prefix = "slice" + Index(sliceIndex);
            sql.Append(predicate.ToString()
                .Replace("{fromTimestamp:", "{" + prefix + "From:", StringComparison.Ordinal)
                .Replace("{toTimestamp:", "{" + prefix + "To:", StringComparison.Ordinal));
            parameters.Add(new(prefix + "From", slice.From));
            parameters.Add(new(prefix + "To", slice.To));
            if (keys != 0)
            {
                sql.Append(" GROUP BY ");
                AppendAliases(sql, keys, trailing: false);
            }
        }
        sql.Append(')');
        if (report is not null)
        {
            ReportShapeSql.AppendGrouping(sql, report);
        }
        return (sql.ToString(), parameters);
    }

    private static void AppendState(StringBuilder sql, NamedMetric metric, Rollup rollup, int index, bool summarized)
    {
        bool conditional = metric.EventType is not null;
        bool nullableCondition = conditional && !summarized && metric.Measure is not null;
        sql.Append(metric.Aggregation).Append(summarized ? "MergeState" : "State");
        if (conditional && !nullableCondition)
        {
            sql.Append("If");
        }
        sql.Append('(');
        string? column = summarized
            ? metric.Measure is null ? "events" : rollup.MeasureColumn(metric.Measure, metric.Aggregation)
            : metric.Measure;
        if (nullableCondition)
        {
            sql.Append("if(event_type = {").Append(MetricAggregateSql.EventTypeParameter(index))
                .Append(":String}, ").Append(column).Append(", NULL)");
        }
        else if (column is not null)
        {
            sql.Append(column);
        }
        if (conditional && !nullableCondition)
        {
            if (column is not null)
            {
                sql.Append(", ");
            }
            sql.Append("event_type = {").Append(MetricAggregateSql.EventTypeParameter(index)).Append(":String}");
        }
        sql.Append(") AS __c").Append(Index(index));
    }

    private static void AppendKeys(StringBuilder sql, SavedReport? report, bool summarized)
    {
        int key = 0;
        if (report?.Interval is string interval)
        {
            string expression = ReportShapeSql.Bucket(interval)!;
            sql.Append(summarized ? expression.Replace("timestamp", "bucket", StringComparison.Ordinal) : expression)
                .Append(" AS __k").Append(Index(key++)).Append(", ");
        }
        foreach (string column in report?.GroupExpressions ?? [])
        {
            sql.Append("toString(").Append(column).Append(") AS __k").Append(Index(key++)).Append(", ");
        }
    }

    private static void AppendAliases(StringBuilder sql, int count, bool trailing)
    {
        for (int index = 0; index < count; index++)
        {
            if (index != 0)
            {
                sql.Append(", ");
            }
            sql.Append("__k").Append(Index(index));
        }
        if (trailing && count != 0)
        {
            sql.Append(", ");
        }
    }

    private static string Index(int value) => value.ToString(CultureInfo.InvariantCulture);
}

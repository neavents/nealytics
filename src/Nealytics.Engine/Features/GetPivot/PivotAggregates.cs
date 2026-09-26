namespace Nealytics.Engine.Features.GetPivot;

using System;

public static class PivotAggregates
{
    public static string Raw(in PivotMetric metric, string condition, bool approximate, bool exact)
    {
        ArgumentNullException.ThrowIfNull(condition);
        bool scoped = condition.Length > 0;

        switch (metric.Kind)
        {
            case PivotMetricKind.Events:
                if (exact)
                {
                    return scoped ? $"uniqExactIf(event_id, {condition})" : "uniqExact(event_id)";
                }

                return scoped ? $"countIf({condition})" : "count()";
            case PivotMetricKind.Sessions:
                return Distinct("session_id", approximate, condition);
            case PivotMetricKind.Users:
                return Distinct("user_id", approximate, condition);
            case PivotMetricKind.Distinct:
                return Distinct(metric.Column!, approximate, condition);
            case PivotMetricKind.Measure:
                string suffix = scoped ? "If" : string.Empty;
                string parameters = metric.AggregationParameters is null ? string.Empty : "(" + metric.AggregationParameters + ")";
                string arguments = scoped ? metric.Column + ", " + condition : metric.Column!;
                return metric.AggregationName + suffix + parameters + "(" + arguments + ")";
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric.Kind, "Unhandled metric kind.");
        }
    }

    public static string Rollup(in PivotMetric metric, string stateColumn, string condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        string function = metric.Kind switch
        {
            PivotMetricKind.Events => "count",
            PivotMetricKind.Sessions => "uniqExact",
            PivotMetricKind.Users => "uniqExact",
            PivotMetricKind.Measure => metric.CanonicalAggregation!,
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric.Kind, "Not routable."),
        };

        return condition.Length == 0
            ? $"{function}Merge({stateColumn})"
            : $"{function}MergeIf({stateColumn}, {condition})";
    }

    public static string? Presence(in PivotMetric metric, string condition, bool rollup)
    {
        ArgumentNullException.ThrowIfNull(condition);

        if (metric.Kind is not (PivotMetricKind.Sessions or PivotMetricKind.Users or PivotMetricKind.Distinct))
        {
            return null;
        }

        if (rollup)
        {
            return condition.Length == 0 ? "countMerge(events)" : $"countMergeIf(events, {condition})";
        }

        return condition.Length == 0 ? "count()" : $"countIf({condition})";
    }

    public static string OrNull(in PivotMetric metric, string value, string? presence)
    {
        if (presence is not null)
        {
            return $"if({presence} = 0, NULL, {value})";
        }

        return metric.Kind == PivotMetricKind.Events
            || string.Equals(metric.AggregationName, "count", StringComparison.Ordinal)
            ? $"nullIf({value}, 0)"
            : value;
    }

    public static string Both(string left, string right) =>
        left.Length == 0 ? right : right.Length == 0 ? left : left + " AND " + right;

    private static string Distinct(string column, bool approximate, string condition)
    {
        string function = approximate ? "uniq" : "uniqExact";
        return condition.Length == 0 ? $"{function}({column})" : $"{function}If({column}, {condition})";
    }
}

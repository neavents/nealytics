namespace Nealytics.Engine.Features.GetBreakdown;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public readonly struct RollupPlan
{
    public Rollup Rollup { get; init; }

    public string ValueExpression { get; init; }

    public bool DropZeroGroups { get; init; }
}

public static class RollupPlanner
{
    public static bool IsAligned(DateTime from, DateTime to, RollupGrain grain)
    {
        if (from >= to)
        {
            return false;
        }

        return grain switch
        {
            RollupGrain.Day => IsMidnight(from) && IsMidnight(to),
            RollupGrain.Hour => IsHourStart(from) && IsHourStart(to),

            RollupGrain.Session => false,
            _ => false,
        };
    }

    private static bool IsMidnight(DateTime value) =>
        value.TimeOfDay == TimeSpan.Zero;

    private static bool IsHourStart(DateTime value) =>
        value.Minute == 0 && value.Second == 0 && value.Millisecond == 0 && value.Ticks % TimeSpan.TicksPerSecond == 0;

    public static RollupPlan? Select(in BreakdownRequest request, RollupRegistry rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        Rollup? best = null;
        string valueExpression = string.Empty;

        foreach (Rollup candidate in rollups.Routable)
        {
            if (!IsAligned(request.From, request.To, candidate.Grain))
            {
                continue;
            }

            if (!candidate.CoversEventType(request.EventType))
            {
                continue;
            }

            if (!candidate.CoversColumn(request.GroupByColumn))
            {
                continue;
            }

            bool filtersCovered = true;

            foreach (QueryFilter filter in request.Filters)
            {
                if (filter.IsMeasure || !candidate.CoversColumn(filter.Column))
                {
                    filtersCovered = false;
                    break;
                }
            }

            if (!filtersCovered)
            {
                continue;
            }

            string? expression = ValueExpressionFor(request, candidate);

            if (expression is null)
            {
                continue;
            }

            if (best is null || candidate.Dimensions.Count < best.Dimensions.Count)
            {
                best = candidate;
                valueExpression = expression;
            }
        }

        if (best is null)
        {
            return null;
        }

        return new RollupPlan
        {
            Rollup = best,
            ValueExpression = valueExpression,
            DropZeroGroups = request.Metric == BreakdownMetric.Users,
        };
    }

    private static string? ValueExpressionFor(in BreakdownRequest request, Rollup rollup)
    {
        switch (request.Metric)
        {
            case BreakdownMetric.Events:
                return "countMerge(events)";

            case BreakdownMetric.Sessions:
                return "uniqExactMerge(sessions)";

            case BreakdownMetric.Users:
                return "uniqExactMerge(users)";

            case BreakdownMetric.Measure:
                if (request.MeasureColumn is null || request.MeasureFunction is null)
                {
                    return null;
                }

                if (!RollupRegistry.SupportedAggregations.Contains(request.MeasureFunction))
                {
                    return null;
                }

                string? column = rollup.MeasureColumn(request.MeasureColumn, request.MeasureFunction);

                return column is null ? null : $"{request.MeasureFunction}Merge({column})";

            default:
                return null;
        }
    }
}

namespace Nealytics.Engine.Features.GetBreakdown;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Configuration;

public readonly struct RollupPlan
{
    public Rollup Rollup { get; init; }

    /// <summary>The <c>-Merge</c> expression that reproduces the requested metric from stored state.</summary>
    public string ValueExpression { get; init; }

    /// <summary>
    /// True when groups whose value is zero must be dropped to match the raw query.
    ///
    /// Only the user metric needs this: raw counts users with <c>user_id IS NOT NULL</c> in the
    /// WHERE clause, so a group containing no known user has no rows at all and never appears. The
    /// rollup keeps that group with a merged count of zero, and without this it would show up as a
    /// row raw would never have returned.
    /// </summary>
    public bool DropZeroGroups { get; init; }
}

public static class RollupPlanner
{
    /// <summary>
    /// Whether a rollup may answer this range at all.
    ///
    /// A rollup row is one whole bucket. Answering 12:00–18:00 from a daily rollup would return the
    /// whole day — a number that is wrong and looks entirely healthy. So a rollup is used only when
    /// both ends sit on a bucket boundary, and the routed query then covers <c>[from, to)</c>.
    /// </summary>
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

            // A session rollup is never a breakdown source. It has no `bucket` column and does not
            // key on event_type, so the query this planner builds would not merely be slow against
            // it — it would not be the same question. Stated rather than left to the catch-all,
            // because the next grain added here will inherit whichever it is.
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

        foreach (Rollup candidate in rollups.Declared)
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

            foreach (BreakdownFilter filter in request.Filters)
            {
                if (!candidate.CoversColumn(filter.Column))
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

                // A rollup stores sum/avg/min/max/count states only. A percentile arrives here as
                // quantile(0.95), which is not a stored state and never will be, so it stays on raw.
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

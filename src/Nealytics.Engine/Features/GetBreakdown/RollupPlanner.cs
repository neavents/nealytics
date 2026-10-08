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

            if (!ServesScope(candidate, request.TenantId, request.TenantSet))
            {
                continue;
            }

            if (!candidate.CoversEventType(request.EventType))
            {
                continue;
            }

            if (!CoversGroup(candidate, request.GroupByColumn, request.TenantSet))
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

            if (best is null || Ranks(candidate, best))
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

    public static bool ServesScope(Rollup rollup, string? tenantId, TenantSet? set)
    {
        if (!rollup.IsGroupRollup)
        {
            return true;
        }

        return set is TenantSet members
            && string.IsNullOrEmpty(tenantId)
            && rollup.HoldsTenantAttribute(members.Attribute);
    }

    public static bool CoversGroup(Rollup rollup, string column, TenantSet? set)
    {
        if (rollup.CoversColumn(column))
        {
            return true;
        }

        if (string.Equals(column, "tenant_id", StringComparison.Ordinal) && set is null)
        {
            return false;
        }

        return rollup.CoversTenantColumn(column);
    }

    public static bool Ranks(Rollup candidate, Rollup best)
    {
        if (candidate.IsGroupRollup != best.IsGroupRollup)
        {
            return candidate.IsGroupRollup;
        }

        return candidate.Dimensions.Count < best.Dimensions.Count;
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
                string? aggregation = request.MeasureAggregation ?? request.MeasureFunction;

                if (request.MeasureColumn is null || aggregation is null)
                {
                    return null;
                }

                if (!RollupRegistry.SupportedAggregations.Contains(aggregation))
                {
                    return null;
                }

                return rollup.MergeExpression(request.MeasureColumn, aggregation, null);

            default:
                return null;
        }
    }
}

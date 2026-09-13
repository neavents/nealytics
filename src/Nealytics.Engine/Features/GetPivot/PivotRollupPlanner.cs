namespace Nealytics.Engine.Features.GetPivot;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public readonly struct PivotRollupPlan
{
    public Rollup Rollup { get; init; }
    public IReadOnlyList<string> StateColumns { get; init; }
}

public static class PivotRollupPlanner
{
    public static PivotRollupPlan? Select(in PivotRequest request, RollupRegistry rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        if (request.Exact || ScopeClause.HasMeasureFilter(request.Filters))
        {
            return null;
        }

        Rollup? best = null;
        List<string>? bestColumns = null;

        foreach (Rollup candidate in rollups.Routable)
        {
            if (candidate.Grain == RollupGrain.Session
                || !RollupPlanner.IsAligned(request.From, request.To, candidate.Grain)
                || !candidate.CoversColumn(request.GroupByColumn)
                || !FiltersCovered(request.Filters, candidate))
            {
                continue;
            }

            List<string>? columns = StateColumns(request.Metrics, candidate);

            if (columns is null)
            {
                continue;
            }

            if (best is null || candidate.Dimensions.Count < best.Dimensions.Count)
            {
                best = candidate;
                bestColumns = columns;
            }
        }

        return best is null ? null : new PivotRollupPlan { Rollup = best, StateColumns = bestColumns! };
    }

    private static bool FiltersCovered(IReadOnlyList<QueryFilter> filters, Rollup rollup)
    {
        foreach (QueryFilter filter in filters)
        {
            if (!rollup.CoversColumn(filter.Column))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string>? StateColumns(IReadOnlyList<PivotMetric> metrics, Rollup rollup)
    {
        List<string> columns = new(metrics.Count);

        foreach (PivotMetric metric in metrics)
        {
            if (!rollup.CoversEventType(metric.EventType))
            {
                return null;
            }

            switch (metric.Kind)
            {
                case PivotMetricKind.Events:
                    columns.Add("events");
                    break;
                case PivotMetricKind.Sessions:
                    columns.Add("sessions");
                    break;
                case PivotMetricKind.Users:
                    columns.Add("users");
                    break;
                case PivotMetricKind.Measure:
                    if (metric.Column is null || metric.CanonicalAggregation is null
                        || !RollupRegistry.SupportedAggregations.Contains(metric.CanonicalAggregation))
                    {
                        return null;
                    }

                    string? stored = rollup.MeasureColumn(metric.Column, metric.CanonicalAggregation);

                    if (stored is null)
                    {
                        return null;
                    }

                    columns.Add(stored);
                    break;
                default:
                    return null;
            }
        }

        return columns;
    }
}

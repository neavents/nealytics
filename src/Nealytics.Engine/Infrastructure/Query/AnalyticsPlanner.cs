namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;

public sealed record AnalyticsPlanCandidate(string Name, bool Eligible, string Reason);
public sealed record AnalyticsPlanSlice(string Source, DateTime From, DateTime To);

internal sealed record AnalyticsPlan(
    string Source, Rollup? Rollup, IReadOnlyList<AnalyticsPlanSlice> Slices, IReadOnlyList<AnalyticsPlanCandidate> Candidates);

public sealed class AnalyticsPlanner
{
    private static readonly DateTime LastBucket = DateTime.UnixEpoch.AddSeconds(uint.MaxValue);
    private readonly RollupRegistry _rollups;
    private readonly DimensionRegistry _dimensions;
    private readonly bool _enabled;
    private readonly bool _appendOnly;
    private readonly int _retentionDays;
    private readonly TimeProvider _clock;

    public AnalyticsPlanner(
        RollupRegistry rollups, DimensionRegistry dimensions, IOptions<TelemetryEngineOptions> options, TimeProvider? clock = null)
    {
        _rollups = rollups;
        _dimensions = dimensions;
        _enabled = options.Value.EnableAnalyticsRollupPlanning;
        _appendOnly = options.Value.AnalyticsRollupSourceIsAppendOnly;
        _retentionDays = (options.Value.EventTypeRetention ?? []).Select(rule => rule.Days)
            .Append(options.Value.RetentionDays).Min();
        _clock = clock ?? TimeProvider.System;
    }

    internal AnalyticsPlan Select(
        IReadOnlyList<NamedMetric> components, in QueryScope scope, SavedReport? report, string execution, bool explain = false)
    {
        if (!explain && (!_enabled || execution == "raw"))
        {
            return new AnalyticsPlan("raw", null, [new("raw", scope.From, scope.To)], Array.Empty<AnalyticsPlanCandidate>());
        }
        List<AnalyticsPlanCandidate> candidates = new(_rollups.Declared.Count);
        Rollup? selected = null;
        DateTime interiorFrom = default;
        DateTime interiorTo = default;
        foreach (Rollup rollup in _rollups.Declared.OrderBy(rollup => rollup.Grain).ThenBy(rollup => rollup.Name, StringComparer.Ordinal))
        {
            string? reason = !_enabled ? "Analytics rollup planning is disabled."
                : execution == "raw" ? "Raw execution was requested."
                : Incompatibility(rollup, components, scope, report);
            long width = rollup.Grain == RollupGrain.Hour ? TimeSpan.TicksPerHour : TimeSpan.TicksPerDay;
            DateTime start = scope.From;
            DateTime end = scope.To;
            if (reason is null)
            {
                start = new DateTime(((scope.From.Ticks + width - 1) / width) * width, DateTimeKind.Utc);
                end = new DateTime((scope.To.Ticks / width) * width, DateTimeKind.Utc);
                if (start >= end)
                {
                    reason = "The range contains no complete rollup bucket.";
                }
            }
            candidates.Add(new AnalyticsPlanCandidate(rollup.Name, reason is null, reason ?? "Compatible aggregate states and verified coverage."));
            if (reason is null && selected is null)
            {
                selected = rollup;
                interiorFrom = start;
                interiorTo = end;
            }
        }
        if (selected is null)
        {
            return new AnalyticsPlan("raw", null, [new("raw", scope.From, scope.To)], candidates.AsReadOnly());
        }
        List<AnalyticsPlanSlice> slices = new(3);
        if (scope.From < interiorFrom)
        {
            slices.Add(new("raw", scope.From, interiorFrom));
        }
        slices.Add(new("rollup", interiorFrom, interiorTo));
        if (interiorTo < scope.To)
        {
            slices.Add(new("raw", interiorTo, scope.To));
        }
        return new AnalyticsPlan(slices.Count == 1 ? "rollup" : "hybrid", selected,
            slices.AsReadOnly(), candidates.AsReadOnly());
    }

    private string? Incompatibility(Rollup rollup, IReadOnlyList<NamedMetric> components, in QueryScope scope, SavedReport? report)
    {
        string? readiness = _rollups.UnroutableReason(rollup);
        if (readiness is not null)
        {
            return "Rollup is not ready: " + readiness;
        }
        if (rollup.Grain == RollupGrain.Session || rollup.IsGroupRollup)
        {
            return "Session and insertion-time tenant-group rollups cannot preserve this query's semantics.";
        }
        if (!_appendOnly)
        {
            return "Append-only source semantics and unique event ordering keys have not been asserted.";
        }
        if (scope.From < DateTime.UnixEpoch || scope.To > LastBucket)
        {
            return "The range exceeds the rollup bucket's DateTime calendar.";
        }
        if (_retentionDays <= 0)
        {
            return "The raw retention policy is not reconciled for analytics planning.";
        }
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        if (_retentionDays <= now.Ticks / TimeSpan.TicksPerDay)
        {
            DateTime cutoff = now.AddDays(-_retentionDays);
            cutoff = new DateTime(cutoff.Ticks - cutoff.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddSeconds(1);
            if (scope.From < cutoff)
            {
                return "The range may include raw rows removed by retention.";
            }
        }
        if (report?.Interval == "hour" && rollup.Grain != RollupGrain.Hour)
        {
            return "The rollup grain is coarser than the report interval.";
        }
        if (components.Count == 0)
        {
            return "Constant-only reports retain raw row-presence semantics.";
        }
        foreach (NamedMetric metric in components)
        {
            if (metric.Aggregation is not ("count" or "sum" or "avg" or "min" or "max") || metric.WidenSum)
            {
                return "A component requires raw aggregation or a wider state type.";
            }
            if (!rollup.CoversEventType(scope.EventType ?? metric.EventType))
            {
                return "The rollup does not cover every component event type.";
            }
            if (metric.Measure is string measure && rollup.MeasureColumn(measure, metric.Aggregation) is null)
            {
                return "The rollup does not contain a required measure state.";
            }
        }
        foreach (QueryFilter filter in scope.Filters ?? [])
        {
            if (filter.IsMeasure || !Covered(rollup, filter.Column))
            {
                return "A row filter is not preserved by this rollup.";
            }
        }
        if (report is not null)
        {
            for (int index = 0; index < report.GroupBy.Count; index++)
            {
                if (report.GroupBy[index] != report.GroupExpressions[index] || !Covered(rollup, report.GroupBy[index]))
                {
                    return "A grouping expression or its null semantics is not preserved by this rollup.";
                }
            }
        }
        return null;
    }

    private bool Covered(Rollup rollup, string column)
    {
        if (TenantAttributeRegistry.IsGroupColumn(column) || column == "tenant_id")
        {
            return true;
        }
        return rollup.CoversColumn(column) && column is not ("user_id" or "object_id")
            && _dimensions.Find(column)?.ClickHouseType.StartsWith("Nullable(", StringComparison.Ordinal) != true;
    }
}

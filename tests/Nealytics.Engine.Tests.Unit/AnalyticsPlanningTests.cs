using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.GetNamedMetric;
using Nealytics.Engine.Features.GetSavedReport;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;

namespace Nealytics.Engine.Tests.Unit;

public sealed class AnalyticsPlanningTests
{
    private static readonly DateTime Start = new(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static TelemetryEngineOptions Options() => new()
    {
        EnableAnalyticsRollupPlanning = true,
        Dimensions = [new() { Name = "tag", Type = "LowCardinality" }, new() { Name = "nullable_tag" }],
        Measures = [new() { Name = "amount", Type = "Float64", Aggregations = "sum,avg,min,max,count,p50,p75,p90,p95,p99" }, new() { Name = "integer", Type = "Int64" }],
        TenantAttributes = [new() { Name = "cohort" }],
        NamedMetrics = [new() { Name = "events", EventType = "action" }, new() { Name = "all_events" }, new() { Name = "total", Aggregation = "sum", Measure = "amount" }],
        Rollups = [new() { Name = "hourly", Grain = "hour", Dimensions = "tag,nullable_tag,user_id,object_id", Measures = "amount:sum,amount:avg,amount:min,amount:max,amount:count" }]
    };

    private static (AnalyticsPlanner Planner, RollupRegistry Rollups, NamedMetricRegistry Metrics, DimensionRegistry Dimensions) Build(TelemetryEngineOptions options, bool ready = true)
    {
        var dimensions = new DimensionRegistry(options);
        var measures = new MeasureRegistry(options, dimensions);
        var rollups = new RollupRegistry(options, dimensions, measures, new TenantAttributeRegistry(options));
        if (ready)
        {
            foreach (var rollup in rollups.Declared)
            {
                rollups.MarkRoutable(rollup);
            }
        }
        return (new AnalyticsPlanner(rollups, dimensions, Microsoft.Extensions.Options.Options.Create(options)), rollups, new NamedMetricRegistry(options, measures), dimensions);
    }

    private static QueryScope Scope(DateTime? from = null, DateTime? to = null) => new()
    {
        ProjectId = "p", TenantId = "t", From = from ?? Start, To = to ?? Start.AddHours(3), Filters = []
    };

    private static SavedReport Shape(string? interval = null, params string[] groups) =>
        new("r", 1, null, []) { Interval = interval, GroupBy = groups, GroupExpressions = groups };

    [Theory]
    [InlineData(0, 180, "rollup", 1)]
    [InlineData(1, 180, "hybrid", 2)]
    [InlineData(0, 181, "hybrid", 2)]
    [InlineData(1, 181, "hybrid", 3)]
    [InlineData(1, 59, "raw", 1)]
    public void BucketInteriorsAndRawEdgesAreDisjointAndCoverTheWholeRange(int fromMinutes, int toMinutes, string source, int count)
    {
        var configured = Build(Options());
        var scope = Scope(Start.AddMinutes(fromMinutes), Start.AddMinutes(toMinutes));
        var plan = configured.Planner.Select([configured.Metrics.Find("events")!], scope, null, "auto");
        Assert.Equal(source, plan.Source);
        Assert.Equal(count, plan.Slices.Count);
        Assert.Equal(scope.From, plan.Slices[0].From);
        Assert.Equal(scope.To, plan.Slices[^1].To);
        for (int index = 1; index < plan.Slices.Count; index++)
        {
            Assert.Equal(plan.Slices[index - 1].To, plan.Slices[index].From);
        }
        Assert.All(plan.Slices, slice => Assert.True(slice.From < slice.To));
        if (source != "raw")
        {
            var interior = Assert.Single(plan.Slices, slice => slice.Source == "rollup");
            Assert.Equal(0, interior.From.Ticks % TimeSpan.TicksPerHour);
            Assert.Equal(0, interior.To.Ticks % TimeSpan.TicksPerHour);
            Assert.Equal("hourly", plan.Rollup!.Name);
            Assert.True(Assert.Single(plan.Candidates).Eligible);
        }
        else
        {
            Assert.Null(plan.Rollup);
            Assert.Equal("The range contains no complete rollup bucket.", Assert.Single(plan.Candidates).Reason);
        }
    }

    [Theory]
    [InlineData("disabled", "disabled")]
    [InlineData("raw", "requested")]
    [InlineData("unready", "not ready")]
    [InlineData("session", "Session")]
    [InlineData("tenant_group", "tenant-group")]
    [InlineData("before_epoch", "calendar")]
    [InlineData("after_calendar", "calendar")]
    [InlineData("coarse", "coarser")]
    [InlineData("constant", "Constant-only")]
    [InlineData("widened", "wider state")]
    [InlineData("quantile", "raw aggregation")]
    [InlineData("missing_measure", "required measure state")]
    [InlineData("missing_type", "event type")]
    [InlineData("measure_filter", "row filter")]
    [InlineData("computed_filter", "row filter")]
    [InlineData("nullable_filter", "row filter")]
    [InlineData("missing_group", "grouping expression")]
    [InlineData("computed_group", "grouping expression")]
    [InlineData("nullable_group", "null semantics")]
    [InlineData("object_group", "null semantics")]
    [InlineData("user_group", "null semantics")]
    public void UnsupportedCandidatesExplainTheirRawFallback(string scenario, string reason)
    {
        var options = Options();
        QueryScope scope = Scope();
        SavedReport? report = null;
        string execution = "auto";
        string metricName = "events";
        switch (scenario)
        {
            case "disabled": options.EnableAnalyticsRollupPlanning = false; break;
            case "raw": execution = "raw"; break;
            case "session": options.Rollups[0].Grain = "session"; options.Rollups[0].Dimensions = "tag"; break;
            case "tenant_group": options.Rollups[0].TenantAttributes = "cohort"; break;
            case "before_epoch": scope = Scope(DateTime.UnixEpoch.AddMilliseconds(-1), Start); break;
            case "after_calendar": scope = Scope(Start, DateTime.UnixEpoch.AddSeconds(uint.MaxValue).AddMilliseconds(1)); break;
            case "coarse": options.Rollups[0].Grain = "day"; report = Shape("hour"); break;
            case "widened": options.NamedMetrics.Add(new() { Name = "wide", Measure = "integer", Aggregation = "sum" }); metricName = "wide"; break;
            case "quantile": options.NamedMetrics.Add(new() { Name = "q", Measure = "amount", Aggregation = "p95" }); metricName = "q"; break;
            case "missing_measure": options.Rollups[0].Measures = "amount:avg"; metricName = "total"; break;
            case "missing_type": options.Rollups[0].EventTypes = "other"; break;
            case "measure_filter": scope = scope with { Filters = [new() { Column = "amount", Value = "0", IsMeasure = true }] }; break;
            case "computed_filter": scope = scope with { Filters = [QueryFilter.Equals("lowerUTF8(country)", "gb")] }; break;
            case "nullable_filter": scope = scope with { Filters = [QueryFilter.Equals("nullable_tag", "")] }; break;
            case "missing_group": report = Shape(null, "country"); break;
            case "nullable_group": report = Shape(null, "nullable_tag"); break;
            case "object_group": report = Shape(null, "object_id"); break;
            case "user_group": report = Shape(null, "user_id"); break;
            case "computed_group": report = Shape(null, "normal") with { GroupExpressions = ["lowerUTF8(tag)"] }; break;
        }
        var configured = Build(options, scenario != "unready");
        NamedMetric[] components = scenario == "constant" ? [] : [configured.Metrics.Find(metricName)!];
        var plan = configured.Planner.Select(components, scope, report, execution);
        Assert.Equal("raw", plan.Source);
        Assert.Null(plan.Rollup);
        var candidate = Assert.Single(plan.Candidates);
        Assert.False(candidate.Eligible);
        Assert.Contains(reason, candidate.Reason);
    }

    [Fact]
    public void TenantMembershipAndCoveredDimensionsRetainTheirOwnParameters()
    {
        var options = Options();
        var configured = Build(options);
        var scope = Scope() with
        {
            TenantId = "", TenantSet = new TenantSet { Attribute = "cohort", Value = "g" },
            Filters = [QueryFilter.Equals("tenant.cohort", "g"), QueryFilter.Equals("tag", "x")]
        };
        var report = Shape("day", "tenant_id", "tag");
        var components = new[] { configured.Metrics.Find("events")!, configured.Metrics.Find("total")! };
        var plan = configured.Planner.Select(components, scope, report, "auto");
        Assert.Equal("rollup", plan.Source);
        var query = AnalyticsPlanSql.Build(plan, components, scope, report);
        Assert.Contains("tenant_id IN (SELECT tenant_id FROM nealytics_core.tenant_attributes FINAL", query.Sql);
        Assert.Contains("countMergeStateIf(events, event_type = {reportEvent0:String}) AS __c0", query.Sql);
        Assert.Contains("sumMergeState(amount_sum) AS __c1", query.Sql);
        Assert.Contains("toString(coalesce(sumMerge(__c1), 0))", query.Sql);
        Assert.Contains("toStartOfDay(toTimeZone(bucket, 'UTC')) AS __k0", query.Sql);
        Assert.Contains("ORDER BY 1 ASC NULLS FIRST, 2 ASC NULLS FIRST, 3 ASC NULLS FIRST LIMIT 100", query.Sql);
        Assert.Equal(query.Parameters.Count, query.Parameters.Select(parameter => parameter.Key).Distinct().Count());
        Assert.DoesNotContain(query.Parameters, parameter => parameter.Key is "fromTimestamp" or "toTimestamp");
        Assert.Contains(query.Parameters, parameter => parameter.Key == "slice0From" && Equals(parameter.Value, scope.From));
        Assert.Equal("action", query.Parameters.Single(parameter => parameter.Key == "reportEvent0").Value);
    }

    [Fact]
    public void SelectionIsDeterministicAndReadinessChangesImmediatelyFenceEveryLogicalPlan()
    {
        var options = Options();
        options.Rollups.Insert(0, new() { Name = "daily", Grain = "day" });
        options.Rollups.Add(new() { Name = "a_hour", Grain = "hour" });
        var configured = Build(options);
        var metric = configured.Metrics.Find("events")!;
        var plan = configured.Planner.Select([metric], Scope(to: Start.AddDays(1)), null, "auto");
        Assert.Equal(new[] { "a_hour", "hourly", "daily" }, plan.Candidates.Select(candidate => candidate.Name));
        Assert.Equal("a_hour", plan.Rollup!.Name);
        configured.Rollups.MarkUnroutable(plan.Rollup, "delivery identity is ambiguous");
        plan = configured.Planner.Select([metric], Scope(), null, "auto");
        Assert.Equal("hourly", plan.Rollup!.Name);
        Assert.Contains("delivery identity is ambiguous", plan.Candidates[0].Reason);
        options.Rollups.Clear();
        Assert.Empty(Build(options).Planner.Select([metric], Scope(), null, "auto").Candidates);
    }

    [Fact]
    public void StateSqlBindsEveryDisjointRangeAndRetainsConditionalNullableCounts()
    {
        var options = Options();
        options.NamedMetrics = [new() { Name = "nonnull", Measure = "amount", Aggregation = "count", EventType = "action" }];
        var configured = Build(options);
        var components = configured.Metrics.Declared;
        var scope = Scope(Start.AddMilliseconds(1), Start.AddHours(3).AddMilliseconds(1));
        var plan = configured.Planner.Select(components, scope, null, "auto");
        var query = AnalyticsPlanSql.Build(plan, components, scope, null);
        Assert.Equal(2, query.Sql.Split(" UNION ALL ").Length - 1);
        Assert.Contains("countState(if(event_type = {reportEvent0:String}, amount, NULL)) AS __c0", query.Sql);
        Assert.Contains("countMergeStateIf(amount_count, event_type = {reportEvent0:String}) AS __c0", query.Sql);
        Assert.Contains("timestamp < {slice0To:DateTime64}", query.Sql);
        Assert.Contains("bucket < {slice1To:DateTime64}", query.Sql);
        Assert.Contains("timestamp >= {slice2From:DateTime64}", query.Sql);
        Assert.Equal(Start.AddHours(1), query.Parameters.Single(parameter => parameter.Key == "slice0To").Value);
        Assert.Equal(Start.AddHours(3), query.Parameters.Single(parameter => parameter.Key == "slice2From").Value);
        Assert.Equal("A state plan requires a rollup.", Assert.Throws<InvalidOperationException>(() =>
            AnalyticsPlanSql.Build(plan with { Rollup = null }, components, scope, null)).Message);
    }
}

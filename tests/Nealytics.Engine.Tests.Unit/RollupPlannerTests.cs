using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public class RollupPlannerTests
{
    private static readonly DateTime Midnight = new(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NextMidnight = new(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc);

    private static RollupRegistry Registry(params RollupOptions[] rollups)
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions =
            [
                new DimensionOptions { Name = "widget_id", Type = "String" },
                new DimensionOptions { Name = "shelf", Type = "LowCardinality" },
            ],
            Measures =
            [
                new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "sum,avg,p95" },
            ],
            Rollups = [.. rollups],
        };

        DimensionRegistry dimensions = new(options);
        return new RollupRegistry(options, dimensions, new MeasureRegistry(options, dimensions));
    }

    private static RollupOptions Daily(string name = "daily", string dims = "widget_id", string types = "view") =>
        new()
        {
            Name = name,
            Grain = "day",
            EventTypes = types,
            Dimensions = dims,
            Measures = "dwell_ms:sum",
        };

    private static BreakdownRequest Request(
        string groupBy = "widget_id",
        string? eventType = "view",
        BreakdownMetric metric = BreakdownMetric.Events,
        DateTime? from = null,
        DateTime? to = null,
        BreakdownFilter[]? filters = null,
        string? measureColumn = null,
        string? measureFunction = null) => new()
        {
            ProjectId = "proj",
            TenantId = "tenant",
            Metric = metric,
            MeasureColumn = measureColumn,
            MeasureFunction = measureFunction,
            MetricWire = "events",
            GroupByColumn = groupBy,
            EventType = eventType,
            Filters = filters ?? [],
            From = from ?? Midnight,
            To = to ?? NextMidnight,
            Limit = 50,
            Order = BreakdownOrder.ValueDescending,
        };

    [Fact]
    public void NoRollups_MeansRaw()
    {
        RollupPlanner.Select(Request(), Registry()).Should().BeNull();
    }

    [Fact]
    public void AMatchingRollup_IsSelected()
    {
        RollupPlan? plan = RollupPlanner.Select(Request(), Registry(Daily()));

        plan.Should().NotBeNull();
        plan!.Value.Rollup.Name.Should().Be("daily");
        plan.Value.ValueExpression.Should().Be("countMerge(events)");
    }

    [Theory]
    [InlineData("2026-08-10T12:00:00Z", "2026-08-13T00:00:00Z")]
    [InlineData("2026-08-10T00:00:00Z", "2026-08-13T18:30:00Z")]
    [InlineData("2026-08-10T00:00:01Z", "2026-08-13T00:00:00Z")]
    public void AnUnalignedRange_FallsBackToRaw(string from, string to)
    {
        RollupPlan? plan = RollupPlanner.Select(
            Request(
                from: DateTime.Parse(from, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                to: DateTime.Parse(to, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)),
            Registry(Daily()));

        plan.Should().BeNull(
            "a rollup row is one whole bucket, so answering 12:00-18:00 from a daily rollup would "
            + "return the whole day and look entirely healthy");
    }

    [Fact]
    public void AnEmptyRange_FallsBackToRaw()
    {
        RollupPlanner.Select(Request(from: Midnight, to: Midnight), Registry(Daily())).Should().BeNull();
    }

    [Fact]
    public void AnHourlyRollup_AcceptsAnHourBoundary()
    {
        RollupOptions hourly = Daily();
        hourly.Grain = "hour";

        DateTime from = new(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);
        DateTime to = new(2026, 8, 10, 17, 0, 0, DateTimeKind.Utc);

        RollupPlanner.Select(Request(from: from, to: to), Registry(hourly)).Should().NotBeNull();
    }

    [Fact]
    public void AnHourlyRollup_RefusesAMinuteOffset()
    {
        RollupOptions hourly = Daily();
        hourly.Grain = "hour";

        DateTime from = new(2026, 8, 10, 9, 30, 0, DateTimeKind.Utc);
        DateTime to = new(2026, 8, 10, 17, 0, 0, DateTimeKind.Utc);

        RollupPlanner.Select(Request(from: from, to: to), Registry(hourly)).Should().BeNull();
    }

    [Fact]
    public void AGroupByTheRollupDoesNotCarry_FallsBackToRaw()
    {
        RollupPlanner.Select(Request(groupBy: "shelf"), Registry(Daily())).Should().BeNull();
    }

    [Fact]
    public void GroupingByEventType_IsAlwaysCovered()
    {
        RollupPlanner.Select(Request(groupBy: "event_type"), Registry(Daily())).Should().NotBeNull(
            "every rollup keys on event_type");
    }

    [Fact]
    public void AFilterTheRollupDoesNotCarry_FallsBackToRaw()
    {
        RollupPlan? plan = RollupPlanner.Select(
            Request(filters: [new BreakdownFilter { Column = "shelf", Value = "top" }]),
            Registry(Daily()));

        plan.Should().BeNull();
    }

    [Fact]
    public void ARequestWithNoEventType_CannotUseARestrictedRollup()
    {
        RollupPlanner.Select(Request(eventType: null), Registry(Daily())).Should().BeNull(
            "the rollup only aggregated 'view', so it would undercount a request spanning everything");
    }

    [Fact]
    public void ARequestWithNoEventType_CanUseAnUnrestrictedRollup()
    {
        RollupPlanner.Select(Request(eventType: null), Registry(Daily(types: ""))).Should().NotBeNull();
    }

    [Fact]
    public void AnEventTypeOutsideTheRollup_FallsBackToRaw()
    {
        RollupPlanner.Select(Request(eventType: "purchase"), Registry(Daily())).Should().BeNull();
    }

    [Theory]
    [InlineData(BreakdownMetric.Events, "countMerge(events)")]
    [InlineData(BreakdownMetric.Sessions, "uniqExactMerge(sessions)")]
    [InlineData(BreakdownMetric.Users, "uniqExactMerge(users)")]
    public void CountMetrics_MapToTheirMergeExpression(BreakdownMetric metric, string expected)
    {
        RollupPlan? plan = RollupPlanner.Select(Request(metric: metric), Registry(Daily()));

        plan!.Value.ValueExpression.Should().Be(expected);
    }

    [Fact]
    public void TheUserMetric_DropsZeroGroups_ToMatchRaw()
    {
        RollupPlan? plan = RollupPlanner.Select(
            Request(metric: BreakdownMetric.Users), Registry(Daily()));

        plan!.Value.DropZeroGroups.Should().BeTrue(
            "raw filters user_id IS NOT NULL in the WHERE, so a group with no known user has no rows "
            + "and never appears; the rollup would otherwise return it with a count of zero");
    }

    [Fact]
    public void AStoredMeasureAggregation_IsRouted()
    {
        RollupPlan? plan = RollupPlanner.Select(
            Request(metric: BreakdownMetric.Measure, measureColumn: "dwell_ms", measureFunction: "sum"),
            Registry(Daily()));

        plan!.Value.ValueExpression.Should().Be("sumMerge(dwell_ms_sum)");
    }

    [Fact]
    public void AMeasureAggregationTheRollupDidNotStore_FallsBackToRaw()
    {
        RollupPlanner.Select(
            Request(metric: BreakdownMetric.Measure, measureColumn: "dwell_ms", measureFunction: "avg"),
            Registry(Daily())).Should().BeNull("the rollup stores sum only");
    }

    [Fact]
    public void APercentile_AlwaysFallsBackToRaw()
    {
        RollupPlanner.Select(
            Request(metric: BreakdownMetric.Measure, measureColumn: "dwell_ms", measureFunction: "quantile(0.95)"),
            Registry(Daily())).Should().BeNull("a rollup cannot store a quantile state for later merging");
    }

    [Fact]
    public void TheNarrowestMatchingRollupWins()
    {
        RollupRegistry registry = Registry(
            Daily("wide", dims: "widget_id,shelf"),
            Daily("narrow", dims: "widget_id"));

        RollupPlan? plan = RollupPlanner.Select(Request(), registry);

        plan!.Value.Rollup.Name.Should().Be("narrow",
            "fewer grouping columns means fewer rows to merge for the same answer");
    }

    [Fact]
    public void TheRoutedQueryUsesAHalfOpenRangeOnTheBucket()
    {
        RollupPlan? plan = RollupPlanner.Select(Request(), Registry(Daily()));

        (string sql, _) = GetBreakdownQuery.BuildQuery(Request(), plan);

        sql.Should().Contain("FROM nealytics_core.rollup_daily");
        sql.Should().Contain("bucket >= {fromTimestamp:DateTime64} AND bucket < {toTimestamp:DateTime64}",
            "every bucket touched must be entirely inside the request, which alignment already guarantees");
        sql.Should().NotContain("global_events");
    }

    [Fact]
    public void TheRoutedQueryKeepsTheSameTotalsShapeAsRaw()
    {
        RollupPlan? plan = RollupPlanner.Select(Request(), Registry(Daily()));

        (string sql, _) = GetBreakdownQuery.BuildQuery(Request(), plan);

        sql.Should().Contain("(SELECT sum(value) FROM grouped) AS grand_total");
        sql.Should().Contain("(SELECT count() FROM grouped) AS group_count",
            "share and truncated must mean the same thing whichever store answered");
    }

    [Fact]
    public void TheRoutedQueryDoesNotReNormaliseTheKey()
    {
        RollupPlan? plan = RollupPlanner.Select(Request(), Registry(Daily()));

        (string sql, _) = GetBreakdownQuery.BuildQuery(Request(), plan);

        sql.Should().Contain("SELECT widget_id AS key");
        sql.Should().NotContain("ifNull(toString(",
            "the materialized view already stored the normalised key, which is why a rollup key and "
            + "a raw key are the same string");
    }

    [Fact]
    public void TheRoutedFilterComparesTheStoredColumnDirectly()
    {
        RollupRegistry registry = Registry(Daily(dims: "widget_id,shelf"));
        BreakdownRequest request = Request(filters: [new BreakdownFilter { Column = "shelf", Value = "top" }]);

        (string sql, var parameters) = GetBreakdownQuery.BuildQuery(request, RollupPlanner.Select(request, registry));

        sql.Should().Contain("AND shelf = {filter0:String}");
        parameters.Should().Contain(p => p.Key == "filter0" && (string)p.Value! == "top");
    }

    [Fact]
    public void WithoutAPlan_TheRawStatementIsUnchanged()
    {
        (string sql, _) = GetBreakdownQuery.BuildQuery(Request(), null);

        sql.Should().Contain("FROM nealytics_core.global_events");
        sql.Should().Contain("timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64}");
    }
}

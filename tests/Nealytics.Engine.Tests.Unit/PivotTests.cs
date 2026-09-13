using FluentAssertions;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public class PivotTests
{
    private static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Midnight = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NextMidnight = new(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);

    private static TelemetryEngineOptions Options(params RollupOptions[] rollups) => new()
    {
        Dimensions =
        [
            new DimensionOptions { Name = "widget_id", Type = "String" },
            new DimensionOptions { Name = "shelf", Type = "LowCardinality" },
        ],
        Measures =
        [
            new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "sum,avg,p95" },
            new MeasureOptions { Name = "position", Type = "UInt16", Aggregations = "avg" },
        ],
        Rollups = [.. rollups],
    };

    private static (QueryColumns Columns, MeasureRegistry Measures, RollupRegistry Rollups) Registries(params RollupOptions[] rollups)
    {
        TelemetryEngineOptions options = Options(rollups);
        DimensionRegistry dimensions = new(options);
        MeasureRegistry measures = new(options, dimensions);
        return (new QueryColumns(dimensions), measures, new RollupRegistry(options, dimensions, measures));
    }

    private static PivotRequestResult Create(
        string groupBy = "widget_id",
        string[]? metrics = null,
        string[]? filters = null,
        string? orderBy = null,
        string? order = null,
        string? traffic = null,
        string? mode = null,
        string? exact = null,
        string? from = null,
        string? to = null)
    {
        (QueryColumns columns, MeasureRegistry measures, _) = Registries();
        return PivotRequestFactory.Create(
            "proj", "tenant", groupBy, metrics ?? ["events"], filters ?? [], from, to, null,
            orderBy, order, traffic, mode, exact, columns, measures, 10_000, 24, Now);
    }

    [Fact]
    public void EveryMetricShapeParses_AndKeepsItsScope()
    {
        PivotRequestResult result = Create(metrics:
            ["events", "sessions:view", "users:open", "distinct(shelf):view", "avg(dwell_ms):dwell", "p95(dwell_ms)"]);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Request.Metrics.Select(m => m.Kind).Should().Equal(
            PivotMetricKind.Events, PivotMetricKind.Sessions, PivotMetricKind.Users,
            PivotMetricKind.Distinct, PivotMetricKind.Measure, PivotMetricKind.Measure);
        result.Request.Metrics[1].EventType.Should().Be("view");
        result.Request.Metrics[3].Column.Should().Be("shelf");
        result.Request.Metrics[4].AggregationName.Should().Be("avg");
        result.Request.Metrics[4].CanonicalAggregation.Should().Be("avg");
        result.Request.Metrics[5].AggregationName.Should().Be("quantile");
        result.Request.Metrics[5].AggregationParameters.Should().Be("0.95");
        result.Request.Metrics.Select(m => m.Grain).Should().Equal("event", "session", "user", "event", "event", "event");
    }

    [Theory]
    [InlineData("median(dwell_ms)")]
    [InlineData("sum(position)")]
    [InlineData("avg(nothing)")]
    [InlineData("distinct(nope)")]
    [InlineData("sessions:")]
    [InlineData("bogus")]
    public void AMetricNobodyDeclaredIsRefusedByName(string metric)
    {
        PivotRequestResult result = Create(metrics: [metric]);

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(400);
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void OrderByNamesAMetricByIndexOrTheKey()
    {
        Create(metrics: ["events", "sessions"], orderBy: "1").Request.OrderByMetric.Should().Be(1);
        Create(metrics: ["events", "sessions"], orderBy: "key").Request.OrderByKey.Should().BeTrue();
        Create(metrics: ["events", "sessions"], orderBy: "key").Request.Descending.Should().BeFalse();
        Create(metrics: ["events", "sessions"], orderBy: "2").Success.Should().BeFalse();
        Create(metrics: ["events"], order: "sideways").Success.Should().BeFalse();
        Create(metrics: ["events"], order: "asc").Request.Descending.Should().BeFalse();
    }

    [Fact]
    public void TooManyMetricsOrNoneIsRefused()
    {
        Create(metrics: []).Success.Should().BeFalse();
        Create(metrics: Enumerable.Repeat("events", PivotRequestFactory.MaxMetrics + 1).ToArray()).Success.Should().BeFalse();
    }

    [Fact]
    public void RawSql_ScopesEachMetricWithItsOwnConditionAndBindsEveryValue()
    {
        PivotRequestResult parsed = Create(
            metrics: ["events", "sessions:view", "users:open", "distinct(shelf):view", "avg(dwell_ms):dwell", "p95(dwell_ms)"],
            filters: ["shelf:top", "dwell_ms>=1000"]);
        parsed.Success.Should().BeTrue(parsed.ErrorMessage);

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = GetPivotQuery.BuildQuery(parsed.Request);

        sql.Should().Contain("ifNull(toString(widget_id), '') AS key");
        sql.Should().Contain("count() AS m0");
        sql.Should().Contain("uniqExactIf(session_id, event_type = {metric1EventType:String}) AS m1");
        sql.Should().Contain("uniqExactIf(user_id, event_type = {metric2EventType:String}) AS m2");
        sql.Should().Contain("uniqExactIf(shelf, event_type = {metric3EventType:String}) AS m3");
        sql.Should().Contain("avgIf(dwell_ms, event_type = {metric4EventType:String}) AS m4");
        sql.Should().Contain("quantile(0.95)(dwell_ms) AS m5");
        sql.Should().Contain("toString(shelf) = {filter0:String}");
        sql.Should().Contain("dwell_ms >= {filter1:Float64}");
        sql.Should().Contain("traffic_class = {trafficClass:String}");
        sql.Should().Contain("totals AS (SELECT");
        sql.Should().Contain("(SELECT m5 FROM totals) AS t5");
        sql.Should().Contain("ORDER BY m0 DESC, key ASC LIMIT {limit:Int32}");
        sql.Should().NotContain("top").And.NotContain("view").And.NotContain("1000");

        parameters.Should().Contain(p => p.Key == "metric1EventType" && Equals(p.Value, "view"));
        parameters.Should().Contain(p => p.Key == "filter0" && Equals(p.Value, "top"));
        parameters.Should().Contain(p => p.Key == "filter1" && Equals(p.Value, 1000d));
        parameters.Should().Contain(p => p.Key == "trafficClass" && Equals(p.Value, "normal"));
    }

    [Fact]
    public void ApproximateAndExactChangeTheFunctions()
    {
        (string approx, _) = GetPivotQuery.BuildQuery(Create(metrics: ["sessions", "distinct(shelf)"], mode: "approx").Request);
        (string exact, _) = GetPivotQuery.BuildQuery(Create(metrics: ["events:view"], exact: "true").Request);

        approx.Should().Contain("uniq(session_id) AS m0").And.Contain("uniq(shelf) AS m1");
        exact.Should().Contain("uniqExactIf(event_id, event_type = {metric0EventType:String}) AS m0");
    }

    [Fact]
    public void TrafficAllDropsTheClause()
    {
        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) =
            GetPivotQuery.BuildQuery(Create(traffic: "all").Request);

        sql.Should().NotContain("traffic_class");
        parameters.Should().NotContain(p => p.Key == "trafficClass");
    }

    private static PivotRequest Routed(RollupRegistry rollups, params string[] metrics)
    {
        (QueryColumns columns, MeasureRegistry measures, _) = Registries();
        PivotRequestResult parsed = PivotRequestFactory.Create(
            "proj", "tenant", "widget_id", metrics, [], Midnight.ToString("O"), NextMidnight.ToString("O"),
            null, null, null, null, null, null, columns, measures, 10_000, 24, Now);
        parsed.Success.Should().BeTrue(parsed.ErrorMessage);
        return parsed.Request;
    }

    private static RollupOptions Daily(string types = "view,open", string measures = "dwell_ms:sum,dwell_ms:avg") => new()
    {
        Name = "daily",
        Grain = "day",
        EventTypes = types,
        Dimensions = "widget_id",
        Measures = measures,
    };

    [Fact]
    public void RollupSql_MergesEachStateUnderItsOwnCondition()
    {
        (_, _, RollupRegistry rollups) = Registries(Daily());
        PivotRequest request = Routed(rollups, "events:view", "sessions:open", "avg(dwell_ms):open");

        PivotRollupPlan? plan = PivotRollupPlanner.Select(request, rollups);
        plan.Should().NotBeNull();

        (string sql, _) = GetPivotQuery.BuildQuery(request, plan);

        sql.Should().Contain("FROM nealytics_core.rollup_daily");
        sql.Should().Contain("countMergeIf(events, event_type = {metric0EventType:String}) AS m0");
        sql.Should().Contain("uniqExactMergeIf(sessions, event_type = {metric1EventType:String}) AS m1");
        sql.Should().Contain("avgMergeIf(dwell_ms_avg, event_type = {metric2EventType:String}) AS m2");
        sql.Should().Contain("widget_id AS key");
        sql.Should().Contain("bucket >= {fromTimestamp:DateTime64} AND bucket < {toTimestamp:DateTime64}");
        sql.Should().Contain("traffic_class = {trafficClass:String}");
    }

    [Fact]
    public void RollupIsRefusedWhenAnyMetricCannotBeAnsweredFromIt()
    {
        (_, _, RollupRegistry rollups) = Registries(Daily());

        PivotRollupPlanner.Select(Routed(rollups, "events:view", "p95(dwell_ms)"), rollups).Should().BeNull("percentiles never route");
        PivotRollupPlanner.Select(Routed(rollups, "events:tap"), rollups).Should().BeNull("tap is not in the rollup");
        PivotRollupPlanner.Select(Routed(rollups, "distinct(shelf)"), rollups).Should().BeNull("a distinct over a column is not stored");
        PivotRollupPlanner.Select(Routed(rollups, "avg(position)"), rollups).Should().BeNull("position is not aggregated there");
        PivotRollupPlanner.Select(Routed(rollups, "sessions"), rollups).Should().BeNull("an unscoped session count over a partial rollup would undercount");
    }

    [Fact]
    public void ARollupCoveringEveryEventTypeAnswersUnscopedMetrics()
    {
        (_, _, RollupRegistry rollups) = Registries(Daily(types: ""));

        PivotRollupPlanner.Select(Routed(rollups, "sessions", "events"), rollups).Should().NotBeNull();
    }

    [Fact]
    public void ADriftedRollupIsNotRouted()
    {
        (_, _, RollupRegistry rollups) = Registries(Daily());
        rollups.MarkUnroutable(rollups.Declared[0], "drift");

        PivotRollupPlanner.Select(Routed(rollups, "events:view"), rollups).Should().BeNull();

        rollups.MarkRoutable(rollups.Declared[0]);
        PivotRollupPlanner.Select(Routed(rollups, "events:view"), rollups).Should().NotBeNull();
    }
}

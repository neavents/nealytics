using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public class BreakdownMeasureMetricTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    private static QueryColumns Columns(params string[] dimensions) =>
        new(new DimensionRegistry(new TelemetryEngineOptions
        {
            Dimensions = [.. dimensions.Select(name => new DimensionOptions { Name = name })],
        }));

    private static MeasureRegistry Measures(
        params (string Name, string Type, string Aggregations)[] measures)
    {
        TelemetryEngineOptions options = new()
        {
            Measures =
            [
                .. measures.Select(m => new MeasureOptions
                {
                    Name = m.Name,
                    Type = m.Type,
                    Aggregations = m.Aggregations,
                }),
            ],
        };

        return new MeasureRegistry(options, new DimensionRegistry(options));
    }

    private static BreakdownRequestResult Create(
        string metric, string groupBy = "widget_id", MeasureRegistry? measures = null)
        => BreakdownRequestFactory.Create(
            "proj", "tenant", metric, groupBy, null, [], null, null, null, null, null, null, null,
            Columns("widget_id"),
            measures ?? Measures(("dwell_ms", "UInt32", "sum,avg,max,p95")),
            maxLimit: 10_000, defaultRangeHours: 24, nowUtc: Now);

    [Theory]
    [InlineData("sum(dwell_ms)", "sum")]
    [InlineData("avg(dwell_ms)", "avg")]
    [InlineData("max(dwell_ms)", "max")]
    public void DeclaredAggregation_ResolvesToItsClickHouseFunction(string metric, string function)
    {
        BreakdownRequestResult result = Create(metric);

        result.Success.Should().BeTrue();
        result.Request.Metric.Should().Be(BreakdownMetric.Measure);
        result.Request.MeasureFunction.Should().Be(function);
        result.Request.MeasureColumn.Should().Be("dwell_ms");
        result.Request.MetricWire.Should().Be(metric);
    }

    [Fact]
    public void PercentileAggregation_ResolvesToAQuantileCall()
    {
        BreakdownRequestResult result = Create("p95(dwell_ms)");

        result.Success.Should().BeTrue();
        result.Request.MeasureFunction.Should().Be("quantile(0.95)");
    }

    [Fact]
    public void MeasureAggregate_ReachesTheSqlAsFunctionThenColumn()
    {
        BreakdownRequestResult result = Create("avg(dwell_ms)");

        (string sql, _) = GetBreakdownQuery.BuildQuery(result.Request);

        sql.Should().Contain("avg(dwell_ms) AS value");
    }

    [Fact]
    public void UndeclaredAggregationForThatMeasure_Is400NamingWhatIsDeclared()
    {
        BreakdownRequestResult result = Create(
            "sum(position)",
            measures: Measures(("position", "UInt16", "avg,min")));

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(400);
        result.ErrorMessage.Should().Contain("'sum' is not an aggregation declared for measure 'position'")
            .And.Contain("avg, min");
    }

    [Fact]
    public void UnknownMeasure_Is400ListingTheDeclaredOnes()
    {
        BreakdownRequestResult result = Create("avg(nope)");

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(400);
        result.ErrorMessage.Should().Contain("'nope' is not a declared measure")
            .And.Contain("dwell_ms");
    }

    [Fact]
    public void NoMeasuresDeclared_SaysSoRatherThanListingNothing()
    {
        TelemetryEngineOptions empty = new();
        BreakdownRequestResult result = Create(
            "avg(dwell_ms)",
            measures: new MeasureRegistry(empty, new DimensionRegistry(empty)));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("declares no measures under TelemetryEngine:Measures");
    }

    [Fact]
    public void RetiredMeasure_IsNotOfferedByTheQueryApi()
    {
        TelemetryEngineOptions options = new()
        {
            Measures = [new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Retired = true }],
        };

        BreakdownRequestResult result = Create(
            "avg(dwell_ms)",
            measures: new MeasureRegistry(options, new DimensionRegistry(options)));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not a declared measure");
    }

    [Fact]
    public void GroupingByAMeasure_Is400ThatExplainsTheKindMistake()
    {
        BreakdownRequestResult result = Create("events", groupBy: "dwell_ms");

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(400);
        result.ErrorMessage.Should()
            .Contain("is a declared measure, not a dimension")
            .And.Contain("metric=avg(dwell_ms)",
                "the caller is confused about kinds, not spelling, so the generic unknown-column "
                + "message would send them looking for a typo that is not there");
    }

    [Theory]
    [InlineData("avg(dwell_ms")]
    [InlineData("avg dwell_ms)")]
    [InlineData("(dwell_ms)")]
    [InlineData("avg()")]
    [InlineData("dwell_ms")]
    public void MalformedMetric_Is400AndNeverReachesTheDatabase(string metric)
    {
        BreakdownRequestResult result = Create(metric);

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(400);
    }

    [Theory]
    [InlineData("avg(dwell_ms); DROP TABLE global_events")]
    [InlineData("sum(dwell_ms) FROM system.tables --")]
    [InlineData("avg(dwell_ms)) UNION SELECT 1,2,3,4 --")]
    public void InjectionAttemptInMetric_Is400(string metric)
    {
        BreakdownRequestResult result = Create(metric);

        result.Success.Should().BeFalse(
            "neither half of a measure aggregate is ever caller input: the function comes from a "
            + "closed map and the column from the registry's own instance");
        result.ErrorStatusCode.Should().Be(400);
    }

    [Fact]
    public void OverlongMetric_Is400BeforeAnyParsing()
    {
        BreakdownRequestResult result = Create("avg(" + new string('a', 300) + ")");

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(400);
    }

    [Fact]
    public void CountMetricsKeepTheirGrain()
    {
        BreakdownRequestFactory.ToGrain(BreakdownMetric.Events).Should().Be("event");
        BreakdownRequestFactory.ToGrain(BreakdownMetric.Sessions).Should().Be("session");
        BreakdownRequestFactory.ToGrain(BreakdownMetric.Users).Should().Be("user");
    }

    [Fact]
    public void MeasureAggregation_IsAtEventGrain()
    {
        BreakdownRequestFactory.ToGrain(BreakdownMetric.Measure).Should().Be(
            "event",
            "a measure is aggregated over rows, so a per-session average needs a session rollup "
            + "rather than this endpoint pretending it has one");
    }
}

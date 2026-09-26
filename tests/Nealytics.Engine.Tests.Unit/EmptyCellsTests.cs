using System.Text.Json;
using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetComparison;
using Nealytics.Engine.Features.GetDistribution;
using Nealytics.Engine.Features.GetEventTimeSeries;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public class EmptyCellsTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private const string From = "2026-09-14T00:00:00Z";
    private const string To = "2026-09-21T00:00:00Z";

    private static readonly RollupOptions Daily = new()
    {
        Name = "daily_by_widget",
        Grain = "day",
        EventTypes = "",
        Dimensions = "widget_id",
        Measures = "dwell_ms:avg,dwell_ms:count",
    };

    private static (QueryColumns Columns, MeasureRegistry Measures, RollupRegistry Rollups) Registries()
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [new DimensionOptions { Name = "widget_id", Type = "String" }],
            Measures = [new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "sum,avg,count,p95" }],
            Rollups = [Daily],
        };
        DimensionRegistry dimensions = new(options);
        MeasureRegistry measures = new(options, dimensions);
        return (new QueryColumns(dimensions), measures, new RollupRegistry(options, dimensions, measures));
    }

    private static PivotRequestResult Pivot(string? empty, params string[] metrics)
    {
        (QueryColumns columns, MeasureRegistry measures, _) = Registries();
        return PivotRequestFactory.Create(
            "proj", "tenant", "widget_id", metrics, [], From, To, null, null, null, null, null, null, empty,
            columns, measures, 10_000, 24, Now);
    }

    private static BreakdownRequestResult Breakdown(string? empty, string metric = "count(dwell_ms)")
    {
        (QueryColumns columns, MeasureRegistry measures, _) = Registries();
        return BreakdownRequestFactory.Create(
            "proj", "tenant", metric, "widget_id", null, [], From, To, null, null, null, null, null, empty,
            columns, measures, 10_000, 24, Now);
    }

    private static ComparisonRequestResult Compare(string? empty, string metric, string? groupBy = null)
    {
        (QueryColumns columns, MeasureRegistry measures, _) = Registries();
        return ComparisonRequestFactory.Create(
            new ComparisonQueryParameters
            {
                ProjectId = "proj",
                TenantId = "tenant",
                Metrics = [metric],
                GroupBy = groupBy,
                Filters = [],
                From = From,
                To = To,
                Empty = empty,
            },
            columns, measures, 10_000, 24, Now);
    }

    private static DistributionRequestResult Distribution(string? empty)
    {
        (QueryColumns columns, MeasureRegistry measures, _) = Registries();
        return DistributionRequestFactory.Create(
            "proj", "tenant", "dwell_ms", null, [], From, To, null, null, null, null, empty, columns, measures, 24, Now);
    }

    private static EventTimeSeriesRequestResult Series(string? empty, string metric)
    {
        (QueryColumns columns, MeasureRegistry measures, _) = Registries();
        return EventTimeSeriesRequestFactory.Create(
            "proj", "tenant", null, "day", From, To, null, null, null, null, [], metric, empty,
            columns, measures, 10_000, 24, Now);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("zero", false)]
    [InlineData("null", true)]
    public void ZeroAndAbsentKeepTheDefault_NullOptsIn(string? raw, bool asNull)
    {
        EmptyCells.TryParse(raw, out bool parsed).Should().BeTrue();
        parsed.Should().Be(asNull);
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("Null")]
    [InlineData("0")]
    [InlineData("none")]
    [InlineData("true")]
    [InlineData(" null")]
    public void AnythingElseIsRefused(string raw)
    {
        EmptyCells.TryParse(raw, out bool parsed).Should().BeFalse();
        parsed.Should().BeFalse();
        EmptyCells.Rejection(raw).Should().Be($"'empty' must be zero or null. Got '{raw}'.");
    }

    [Fact]
    public void EveryReadCarriesTheChoice()
    {
        Pivot(null, "events").Request.EmptyAsNull.Should().BeFalse();
        Pivot("null", "events").Request.EmptyAsNull.Should().BeTrue();
        Breakdown(null).Request.EmptyAsNull.Should().BeFalse();
        Breakdown("null").Request.EmptyAsNull.Should().BeTrue();
        Compare(null, "events").Request.EmptyAsNull.Should().BeFalse();
        Compare("null", "events").Request.EmptyAsNull.Should().BeTrue();
        Distribution(null).Request.EmptyAsNull.Should().BeFalse();
        Distribution("null").Request.EmptyAsNull.Should().BeTrue();
        Series(null, "events").Request.EmptyAsNull.Should().BeFalse();
        Series("null", "events").Request.EmptyAsNull.Should().BeTrue();
    }

    [Fact]
    public void EveryReadRefusesAnUnknownValueWithABadRequest()
    {
        const string message = "'empty' must be zero or null. Got 'nil'.";

        PivotRequestResult pivot = Pivot("nil", "events");
        pivot.Success.Should().BeFalse();
        pivot.ErrorStatusCode.Should().Be(PivotRequestFactory.StatusBadRequest);
        pivot.ErrorMessage.Should().Be(message);

        BreakdownRequestResult breakdown = Breakdown("nil");
        breakdown.ErrorStatusCode.Should().Be(BreakdownRequestFactory.StatusBadRequest);
        breakdown.ErrorMessage.Should().Be(message);

        ComparisonRequestResult compare = Compare("nil", "events");
        compare.ErrorStatusCode.Should().Be(ComparisonRequestFactory.StatusBadRequest);
        compare.ErrorMessage.Should().Be(message);

        DistributionRequestResult distribution = Distribution("nil");
        distribution.ErrorStatusCode.Should().Be(DistributionRequestFactory.StatusBadRequest);
        distribution.ErrorMessage.Should().Be(message);

        EventTimeSeriesRequestResult series = Series("nil", "events");
        series.ErrorStatusCode.Should().Be(EventTimeSeriesRequestFactory.StatusBadRequest);
        series.ErrorMessage.Should().Be(message);
    }

    [Fact]
    public void ExplicitZeroBuildsTheSameStatementAsNoParameter()
    {
        string[] metrics = ["events:view", "sessions:view", "users", "distinct(widget_id):open", "avg(dwell_ms)", "count(dwell_ms):view"];
        (_, _, RollupRegistry rollups) = Registries();

        GetPivotQuery.BuildQuery(Pivot("zero", metrics).Request).Sql
            .Should().Be(GetPivotQuery.BuildQuery(Pivot(null, metrics).Request).Sql);
        GetBreakdownQuery.BuildQuery(Breakdown("zero").Request).Sql
            .Should().Be(GetBreakdownQuery.BuildQuery(Breakdown(null).Request).Sql);
        GetComparisonQuery.BuildQuery(Compare("zero", "sessions:view", "widget_id").Request, null).Sql
            .Should().Be(GetComparisonQuery.BuildQuery(Compare(null, "sessions:view", "widget_id").Request, null).Sql);
        GetComparisonQuery.BuildQuery(Compare("zero", "users").Request, null).Sql
            .Should().Be(GetComparisonQuery.BuildQuery(Compare(null, "users").Request, null).Sql);
        GetEventTimeSeriesQuery.BuildQuery(Series("zero", "users:view").Request).Sql
            .Should().Be(GetEventTimeSeriesQuery.BuildQuery(Series(null, "users:view").Request).Sql);

        PivotRequest routed = Pivot(null, "events:view", "avg(dwell_ms)").Request;
        PivotRollupPlan? plan = PivotRollupPlanner.Select(routed, rollups);
        plan.Should().NotBeNull();
        GetPivotQuery.BuildQuery(Pivot("zero", "events:view", "avg(dwell_ms)").Request, plan).Sql
            .Should().Be(GetPivotQuery.BuildQuery(routed, plan).Sql);
    }

    [Fact]
    public void APivotCellIsNullOnlyWhenNothingMatchedIt_AndStillSortsOnTheCount()
    {
        (string sql, _) = GetPivotQuery.BuildQuery(Pivot(
            "null", "events:view", "sessions:view", "users", "distinct(widget_id):open", "avg(dwell_ms)", "count(dwell_ms):view", "sum(dwell_ms)").Request);

        sql.Should().Contain("countIf(event_type = {metric0EventType:String}) AS m0,");
        sql.Should().Contain("uniqExactIf(session_id, event_type = {metric1EventType:String}) AS m1, countIf(event_type = {metric1EventType:String}) AS n1");
        sql.Should().Contain("uniqExact(user_id) AS m2, count() AS n2");
        sql.Should().Contain("AS m3, countIf(event_type = {metric3EventType:String}) AS n3");
        sql.Should().NotContain(" AS n0").And.NotContain(" AS n4").And.NotContain(" AS n5").And.NotContain(" AS n6");
        sql.Should().Contain(
            "SELECT key, nullIf(m0, 0), if(n1 = 0, NULL, m1), if(n2 = 0, NULL, m2), if(n3 = 0, NULL, m3), m4, nullIf(m5, 0), m6,");
        sql.Should().Contain("(SELECT nullIf(m0, 0) FROM totals) AS t0");
        sql.Should().Contain("(SELECT if(n1 = 0, NULL, m1) FROM totals) AS t1");
        sql.Should().Contain("(SELECT m4 FROM totals) AS t4");
        sql.Should().Contain("ORDER BY m0 DESC, key ASC");
    }

    [Fact]
    public void ARoutedPivotCountsItsRowsFromTheRollupsEventState()
    {
        (_, _, RollupRegistry rollups) = Registries();
        PivotRequest request = Pivot("null", "sessions:view", "count(dwell_ms)").Request;
        PivotRollupPlan? plan = PivotRollupPlanner.Select(request, rollups);

        plan.Should().NotBeNull();
        (string sql, _) = GetPivotQuery.BuildQuery(request, plan);

        sql.Should().Contain("uniqExactMergeIf(sessions, event_type = {metric0EventType:String}) AS m0, countMergeIf(events, event_type = {metric0EventType:String}) AS n0");
        sql.Should().Contain("SELECT key, if(n0 = 0, NULL, m0), nullIf(m1, 0),");
        sql.Should().NotContain("global_events");
    }

    [Fact]
    public void ABreakdownOnlyRewritesACountOfAMeasure()
    {
        (string counted, _) = GetBreakdownQuery.BuildQuery(Breakdown("null").Request);
        counted.Should().Contain("SELECT key, nullIf(value, 0), (SELECT nullIf(sum(value), 0) FROM grouped) AS grand_total,");
        counted.Should().Contain("ORDER BY value DESC");

        foreach (string metric in new[] { "events", "sessions", "users", "avg(dwell_ms)", "sum(dwell_ms)" })
        {
            GetBreakdownQuery.BuildQuery(Breakdown("null", metric).Request).Sql
                .Should().Be(GetBreakdownQuery.BuildQuery(Breakdown(null, metric).Request).Sql, metric);
        }
    }

    [Fact]
    public void AComparisonNullsAWindowWithNoRowsOnEitherShape()
    {
        (string ungrouped, _) = GetComparisonQuery.BuildQuery(Compare("null", "sessions").Request, null);
        ungrouped.Should().StartWith(
            "SELECT if(countIf((timestamp >= {fromTimestamp:DateTime64} AND timestamp < {toTimestamp:DateTime64})) = 0, NULL, "
            + "uniqExactIf(session_id, (timestamp >= {fromTimestamp:DateTime64} AND timestamp < {toTimestamp:DateTime64}))) AS c, ");

        (string grouped, _) = GetComparisonQuery.BuildQuery(Compare("null", "events", "widget_id").Request, null);
        grouped.Should().Contain(" SELECT key, nullIf(c, 0), nullIf(p, 0), (SELECT nullIf(c, 0) FROM totals) AS tc, (SELECT nullIf(p, 0) FROM totals) AS tp,");
        grouped.Should().Contain("ORDER BY c DESC");

        (string distinct, _) = GetComparisonQuery.BuildQuery(Compare("null", "users", "widget_id").Request, null);
        distinct.Should().Contain(" AS p, countIf((timestamp >= {fromTimestamp:DateTime64}");
        distinct.Should().Contain(" SELECT key, if(nc = 0, NULL, c), if(np = 0, NULL, p),");
    }

    [Fact]
    public void ASeriesValueIsNullForABucketWithoutAMatchingRow()
    {
        GetEventTimeSeriesQuery.BuildQuery(Series("null", "events:view").Request).Sql
            .Should().Contain("nullIf(countIf(event_type = {metricEventType:String}), 0) AS value");
        GetEventTimeSeriesQuery.BuildQuery(Series("null", "users:view").Request).Sql
            .Should().Contain("if(countIf(event_type = {metricEventType:String}) = 0, NULL, uniqExactIf(user_id, event_type = {metricEventType:String})) AS value");
        GetEventTimeSeriesQuery.BuildQuery(Series("null", "avg(dwell_ms)").Request).Sql
            .Should().Contain("avg(dwell_ms) AS value");
    }

    [Fact]
    public void ADefaultResponseSerialisesExactlyAsBefore()
    {
        PivotResponse pivot = new()
        {
            Totals = [3, 0],
            Rows = [new PivotRow { Key = "a", Values = [3, 0] }],
        };
        BreakdownResponse breakdown = new()
        {
            Total = 4,
            Rows = [new BreakdownRow { Key = "a", Value = 1, Share = 0.25 }, new BreakdownRow { Key = "b", Value = 0 }],
        };
        DistributionResponse distribution = new() { Quantiles = [new DistributionQuantile { Q = 0.5, Value = 0 }] };

        string pivotJson = JsonSerializer.Serialize(pivot, TelemetryAotContext.Default.PivotResponse);
        string breakdownJson = JsonSerializer.Serialize(breakdown, TelemetryAotContext.Default.BreakdownResponse);
        string distributionJson = JsonSerializer.Serialize(distribution, TelemetryAotContext.Default.DistributionResponse);

        pivotJson.Should().Contain("\"totals\":[3,0]").And.Contain("\"rows\":[{\"key\":\"a\",\"values\":[3,0]}]");
        breakdownJson.Should().Contain("\"total\":4").And.Contain(
            "\"rows\":[{\"key\":\"a\",\"value\":1,\"share\":0.25},{\"key\":\"b\",\"value\":0,\"share\":0}]");
        distributionJson.Should().Contain("\"min\":0,\"max\":0,\"avg\":0,\"quantiles\":[{\"q\":0.5,\"value\":0}]");
    }

    [Fact]
    public void AnEmptyCellSerialisesAsAnExplicitNull()
    {
        PivotResponse pivot = new()
        {
            Totals = [3, null],
            Rows = [new PivotRow { Key = "a", Values = [null, 0] }],
        };
        BreakdownResponse breakdown = new()
        {
            Total = null,
            Rows = [new BreakdownRow { Key = "a", Value = null, Share = null }],
        };
        DistributionResponse distribution = new()
        {
            Min = null,
            Max = null,
            Avg = null,
            Quantiles = [new DistributionQuantile { Q = 0.5, Value = null }],
        };

        JsonSerializer.Serialize(pivot, TelemetryAotContext.Default.PivotResponse)
            .Should().Contain("\"totals\":[3,null]").And.Contain("\"values\":[null,0]");
        JsonSerializer.Serialize(breakdown, TelemetryAotContext.Default.BreakdownResponse)
            .Should().Contain("\"total\":null").And.Contain("{\"key\":\"a\",\"value\":null,\"share\":null}");
        JsonSerializer.Serialize(distribution, TelemetryAotContext.Default.DistributionResponse)
            .Should().Contain("\"min\":null,\"max\":null,\"avg\":null,\"quantiles\":[{\"q\":0.5,\"value\":null}]");
    }
}

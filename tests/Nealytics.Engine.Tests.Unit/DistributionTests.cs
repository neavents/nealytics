using FluentAssertions;
using Nealytics.Engine.Features.GetDistribution;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public class DistributionTests
{
    private static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    private static (QueryColumns Columns, MeasureRegistry Measures) Registries()
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [new DimensionOptions { Name = "widget_id", Type = "String" }],
            Measures = [new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "sum,avg" }],
        };
        DimensionRegistry dimensions = new(options);
        return (new QueryColumns(dimensions), new MeasureRegistry(options, dimensions));
    }

    private static DistributionRequestResult Create(
        string? of, string? eventType = null, string[]? filters = null, string? quantiles = null,
        string? buckets = null, string? traffic = null, string? mode = null)
    {
        (QueryColumns columns, MeasureRegistry measures) = Registries();
        return DistributionRequestFactory.Create(
            "proj", "tenant", of, eventType, filters ?? [], null, null, quantiles, buckets, traffic, mode,
            columns, measures, 24, Now);
    }

    [Fact]
    public void AMeasureASessionDurationAndASessionEventCountAreTheThreeSubjects()
    {
        Create("dwell_ms", "item_dwell").Request.Subject.Should().Be(DistributionSubject.Measure);
        Create("session_duration").Request.Subject.Should().Be(DistributionSubject.SessionDuration);
        Create("session_events").Request.Subject.Should().Be(DistributionSubject.SessionEvents);
        Create("nothing").Success.Should().BeFalse();
        Create(null).Success.Should().BeFalse();
        Create("widget_id").Success.Should().BeFalse("a dimension has no distribution");
    }

    [Fact]
    public void QuantilesDefaultAndAreBounded()
    {
        Create("dwell_ms").Request.Quantiles.Should().Equal(0.5, 0.75, 0.9, 0.95);
        Create("dwell_ms", quantiles: "0.5, 0.99").Request.Quantiles.Should().Equal(0.5, 0.99);
        Create("dwell_ms", quantiles: "1.5").Success.Should().BeFalse();
        Create("dwell_ms", quantiles: "a").Success.Should().BeFalse();
        Create("dwell_ms", quantiles: string.Join(',', Enumerable.Repeat("0.5", DistributionRequestFactory.MaxQuantiles + 1))).Success.Should().BeFalse();
    }

    [Fact]
    public void BucketEdgesMustAscend()
    {
        Create("dwell_ms", buckets: "1000,5000,30000").Request.Edges.Should().Equal(1000, 5000, 30000);
        Create("dwell_ms", buckets: "5000,1000").Success.Should().BeFalse();
        Create("dwell_ms", buckets: "1000,1000").Success.Should().BeFalse();
        Create("dwell_ms").Request.Edges.Should().BeEmpty();
    }

    [Fact]
    public void AnEventTypeOnlyMakesSenseForAMeasure()
    {
        Create("session_duration", "open").Success.Should().BeFalse();
    }

    [Fact]
    public void MeasureSql_SkipsNullsAndBindsEdges()
    {
        DistributionRequestResult parsed = Create("dwell_ms", "item_dwell", ["widget_id:w1"], "0.5,0.9", "1000,5000");
        parsed.Success.Should().BeTrue(parsed.ErrorMessage);

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = GetDistributionQuery.BuildQuery(parsed.Request);

        sql.Should().Contain("quantilesExact(0.5, 0.9)(v) AS q");
        sql.Should().Contain("countIf(v < {edge0:Float64}) AS b0");
        sql.Should().Contain("countIf(v >= {edge0:Float64} AND v < {edge1:Float64}) AS b1");
        sql.Should().Contain("countIf(v >= {edge1:Float64}) AS b2");
        sql.Should().Contain("SELECT toFloat64(dwell_ms) AS v FROM nealytics_core.global_events");
        sql.Should().Contain("event_type = {eventType:String}");
        sql.Should().Contain("toString(widget_id) = {filter0:String}");
        sql.Should().Contain("dwell_ms IS NOT NULL");
        parameters.Should().Contain(p => p.Key == "edge1" && Equals(p.Value, 5000d));
        parameters.Should().Contain(p => p.Key == "eventType" && Equals(p.Value, "item_dwell"));
    }

    [Fact]
    public void SessionSql_DerivesTheValuePerSession()
    {
        (string duration, _) = GetDistributionQuery.BuildQuery(Create("session_duration").Request);
        (string events, _) = GetDistributionQuery.BuildQuery(Create("session_events", mode: "approx").Request);

        duration.Should().Contain("dateDiff('millisecond', min(timestamp), max(timestamp))");
        duration.Should().Contain("GROUP BY session_id");
        duration.Should().Contain("traffic_class = {trafficClass:String}");
        events.Should().Contain("toFloat64(count()) AS v");
        events.Should().Contain("quantilesTDigest(");
        duration.Should().NotContain("countIf", "no edges were asked for");
    }
}

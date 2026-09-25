using FluentAssertions;
using Nealytics.Engine.Features.GetEventTimeSeries;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public class MeasureTimeSeriesTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private static (QueryColumns Columns, MeasureRegistry Measures) Registries()
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [new DimensionOptions { Name = "shelf", Type = "LowCardinality" }],
            Measures =
            [
                new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "sum,avg,p50,p75,p95" },
                new MeasureOptions { Name = "amount", Type = "Decimal", Aggregations = "sum,avg" },
            ],
        };
        DimensionRegistry dimensions = new(options);
        return (new QueryColumns(dimensions), new MeasureRegistry(options, dimensions));
    }

    private static EventTimeSeriesRequestResult Create(string? metric, string? groupBy = null, string? eventType = null)
    {
        (QueryColumns columns, MeasureRegistry measures) = Registries();
        return EventTimeSeriesRequestFactory.Create(
            "proj", "tenant", null, "day", "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z", eventType, groupBy, null, null,
            [], metric, columns, measures, 10_000, 24, Now);
    }

    [Fact]
    public void WithoutAMetricTheSeriesIsUnchanged()
    {
        EventTimeSeriesRequestResult parsed = Create(null);

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = GetEventTimeSeriesQuery.BuildQuery(parsed.Request);

        parsed.Request.Metric.Should().BeNull();
        sql.Should().Be(
            "SELECT toStartOfDay(timestamp) AS bucket, count() AS event_count FROM nealytics_core.global_events"
            + " WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}"
            + " AND timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64}"
            + " AND traffic_class = {trafficClass:String}"
            + " GROUP BY bucket ORDER BY bucket ASC LIMIT {limit:Int32}");
        parameters.Should().NotContain(p => p.Key == "metricEventType");
    }

    [Theory]
    [InlineData("avg(dwell_ms)", "avg(dwell_ms)")]
    [InlineData("sum(amount)", "sum(amount)")]
    [InlineData("p50(dwell_ms)", "quantile(0.5)(dwell_ms)")]
    [InlineData("p75(dwell_ms)", "quantile(0.75)(dwell_ms)")]
    [InlineData("p95(dwell_ms)", "quantile(0.95)(dwell_ms)")]
    [InlineData("sessions", "uniqExact(session_id)")]
    [InlineData("distinct(shelf)", "uniqExact(shelf)")]
    public void AMeasureIsAggregatedPerBucketBesideTheCount(string metric, string expression)
    {
        EventTimeSeriesRequestResult parsed = Create(metric);

        (string sql, _) = GetEventTimeSeriesQuery.BuildQuery(parsed.Request);

        parsed.Success.Should().BeTrue(parsed.ErrorMessage);
        sql.Should().StartWith(
            "SELECT toStartOfDay(timestamp) AS bucket, count() AS event_count, " + expression
            + " AS value FROM nealytics_core.global_events WHERE ");
    }

    [Fact]
    public void AScopedMetricCarriesItsOwnEventTypeAndAGroupedSeriesKeepsItsColumns()
    {
        EventTimeSeriesRequestResult parsed = Create("avg(dwell_ms):dwell", groupBy: "shelf", eventType: "dwell");

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = GetEventTimeSeriesQuery.BuildQuery(parsed.Request);

        sql.Should().StartWith(
            "SELECT toStartOfDay(timestamp) AS bucket, shelf AS series, count() AS event_count, "
            + "avgIf(dwell_ms, event_type = {metricEventType:String}) AS value FROM ");
        sql.Should().EndWith(" GROUP BY bucket, series ORDER BY bucket ASC, series ASC LIMIT {limit:Int32}");
        parameters.Should().Contain(p => p.Key == "metricEventType" && Equals(p.Value, "dwell"));
    }

    [Theory]
    [InlineData("p99(dwell_ms)")]
    [InlineData("avg(nothing)")]
    [InlineData("median(dwell_ms)")]
    [InlineData("sessions:")]
    public void AnUndeclaredMetricIsRefused(string metric)
    {
        EventTimeSeriesRequestResult parsed = Create(metric);

        parsed.Success.Should().BeFalse();
        parsed.ErrorStatusCode.Should().Be(400);
    }

    [Fact]
    public void AnOverlongMetricIsRefused()
    {
        Create(new string('a', 257)).Success.Should().BeFalse();
    }
}

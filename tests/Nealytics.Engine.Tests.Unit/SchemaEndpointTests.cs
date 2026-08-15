using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetSchema;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public class SchemaEndpointTests
{
    private static (BreakdownColumns Columns, DimensionRegistry Dimensions, MeasureRegistry Measures) Build()
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions =
            [
                new DimensionOptions { Name = "widget_id", Type = "String" },
                new DimensionOptions { Name = "gone_id", Type = "String", Retired = true },
            ],
            Measures =
            [
                new MeasureOptions
                {
                    Name = "dwell_ms", Type = "UInt32",
                    Aggregations = "sum,avg,p95", Minimum = 0, Maximum = 1_800_000,
                },
                new MeasureOptions { Name = "old_ms", Type = "UInt32", Retired = true },
            ],
        };

        DimensionRegistry dimensions = new(options);
        return (new BreakdownColumns(dimensions), dimensions, new MeasureRegistry(options, dimensions));
    }

    [Fact]
    public void ActiveDimensionsAreListed_RetiredOnesAreNot()
    {
        (_, DimensionRegistry dimensions, _) = Build();

        dimensions.Active.Select(d => d.Name).Should().Equal("widget_id");
    }

    [Fact]
    public void ActiveMeasuresCarryTheirDeclaredAggregationsAndBounds()
    {
        (_, _, MeasureRegistry measures) = Build();

        Measure measure = measures.Find("dwell_ms")!;
        measure.Aggregations.Order(StringComparer.Ordinal).Should().Equal("avg", "p95", "sum");
        measure.Minimum.Should().Be(0);
        measure.Maximum.Should().Be(1_800_000);
        measures.Find("old_ms").Should().BeNull("a retired measure is not offered by the query API");
    }

    [Fact]
    public void EveryAdvertisedMetricIsAcceptedByTheBreakdownFactory()
    {
        (BreakdownColumns columns, _, MeasureRegistry measures) = Build();

        string[] advertised =
        [
            "events", "sessions", "users",
            "avg(dwell_ms)", "p95(dwell_ms)", "sum(dwell_ms)",
        ];

        foreach (string metric in advertised)
        {
            BreakdownRequestResult result = BreakdownRequestFactory.Create(
                "proj", "tenant", metric, "widget_id", null, [], null, null, null, null, null, null, null,
                columns, measures, 10_000, 24, new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc));

            result.Success.Should().BeTrue(
                "the schema endpoint advertises {0}, so a caller that trusts it must not get a 400",
                metric);
        }
    }

    [Fact]
    public void RetiredMeasureAggregation_IsNotAdvertisedAndIsRefused()
    {
        (BreakdownColumns columns, _, MeasureRegistry measures) = Build();

        BreakdownRequestResult result = BreakdownRequestFactory.Create(
            "proj", "tenant", "avg(old_ms)", "widget_id", null, [], null, null, null, null, null, null, null,
            columns, measures, 10_000, 24, new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc));

        result.Success.Should().BeFalse();
    }
}

/// <summary>
/// What /schema says about the rollups a deployment has declared.
///
/// A rollup is the difference between a chart that loads and one that scans the whole table, and a
/// client building itself from this endpoint has no other way to learn one exists — or that its
/// range has to land on a bucket boundary to benefit from it. Reporting them is what makes a
/// config-driven engine usable by something that was not written against this specific deployment.
/// </summary>
public class SchemaRollupReportingTests
{
    private static Rollup Build(string grain, string eventTypes = "", string measures = "dwell_ms:sum")
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [new DimensionOptions { Name = "widget_id", Type = "String" }],
            Measures = [new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "sum,max" }],
            Rollups =
            [
                new RollupOptions
                {
                    Name = "r", Grain = grain, Dimensions = "widget_id",
                    EventTypes = eventTypes, Measures = measures,
                },
            ],
        };

        DimensionRegistry dimensions = new(options);
        return new RollupRegistry(options, dimensions, new MeasureRegistry(options, dimensions)).Declared[0];
    }

    [Theory]
    [InlineData("hour", "hour")]
    [InlineData("day", "day")]
    [InlineData("session", "session")]
    public void TheGrainIsReportedAsAWireString(string declared, string reported)
    {
        // These are a contract: a client decides whether to align a range on them. Renaming the C#
        // enum must not silently rename a field in somebody's dashboard.
        SchemaRollup.From(Build(declared)).Grain.Should().Be(reported);
    }

    [Fact]
    public void DimensionsAndMeasureStatesAreNamed()
    {
        SchemaRollup rollup = SchemaRollup.From(Build("day", measures: "dwell_ms:sum,dwell_ms:max"));

        rollup.Dimensions.Should().Equal("widget_id");
        rollup.Measures.Should().BeEquivalentTo(["dwell_ms:sum", "dwell_ms:max"]);
    }

    [Fact]
    public void AnEmptyEventTypeListMeansEveryEventType()
    {
        // Reported as empty rather than as the full list, because the rollup genuinely has no
        // filter — enumerating every type seen so far would turn an open set into a closed one.
        SchemaRollup.From(Build("day")).EventTypes.Should().BeEmpty();
    }

    [Fact]
    public void ADeclaredEventTypeFilterIsReported()
    {
        SchemaRollup.From(Build("day", eventTypes: "item_view,item_impression"))
            .EventTypes.Should().BeEquivalentTo(["item_impression", "item_view"]);
    }
}

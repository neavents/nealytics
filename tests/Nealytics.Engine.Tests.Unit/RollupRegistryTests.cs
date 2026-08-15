using FluentAssertions;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public class RollupRegistryTests
{
    private static TelemetryEngineOptions BaseOptions(params RollupOptions[] rollups) => new()
    {
        Dimensions =
        [
            new DimensionOptions { Name = "widget_id", Type = "String" },
            new DimensionOptions { Name = "shelf", Type = "LowCardinality" },
        ],
        Measures =
        [
            new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "sum,avg,max,p95" },
            new MeasureOptions { Name = "price", Type = "Decimal", Aggregations = "sum" },
        ],
        Rollups = [.. rollups],
    };

    private static RollupRegistry Build(params RollupOptions[] rollups)
    {
        TelemetryEngineOptions options = BaseOptions(rollups);
        DimensionRegistry dimensions = new(options);
        return new RollupRegistry(options, dimensions, new MeasureRegistry(options, dimensions));
    }

    private static RollupOptions Valid() => new()
    {
        Name = "daily_by_widget",
        Grain = "day",
        EventTypes = "view,impression",
        Dimensions = "widget_id,shelf",
        Measures = "dwell_ms:sum,price:sum",
    };

    [Fact]
    public void EmptyDeclaration_IsValid()
    {
        Build().Declared.Should().BeEmpty();
    }

    [Fact]
    public void ADeclaredRollup_ResolvesItsTableAndViewNames()
    {
        Rollup rollup = Build(Valid()).Declared.Single();

        rollup.TableName.Should().Be("rollup_daily_by_widget");
        rollup.ViewName.Should().Be("rollup_daily_by_widget_mv");
        rollup.BucketFunction.Should().Be("toStartOfDay");
    }

    [Fact]
    public void HourGrain_UsesTheHourBucket()
    {
        RollupOptions options = Valid();
        options.Grain = "hour";

        Build(options).Declared.Single().BucketFunction.Should().Be("toStartOfHour");
    }

    [Fact]
    public void UnknownGrain_RefusesTheBoot()
    {
        RollupOptions options = Valid();
        options.Grain = "week";

        Action build = () => Build(options);
        build.Should().Throw<InvalidOperationException>().WithMessage("*unknown grain*");
    }

    [Fact]
    public void AnUndeclaredDimension_RefusesTheBoot()
    {
        RollupOptions options = Valid();
        options.Dimensions = "widget_id,nope";

        Action build = () => Build(options);
        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*neither an active declared dimension nor a groupable core column*");
    }

    [Fact]
    public void ACoreColumnIsGroupable_BecauseObjectIdIsTheItemIdentity()
    {
        RollupOptions options = Valid();
        options.Dimensions = "object_id,widget_id";

        Rollup rollup = Build(options).Declared.Single();

        rollup.Dimensions.Select(d => d.Name).Should().Equal("object_id", "widget_id");
        rollup.CoversColumn("object_id").Should().BeTrue(
            "a rollup that cannot key on object_id cannot answer the item report it exists for");

        RollupRegistry.BuildTableDdl(rollup).Should().Contain("object_id String")
            .And.NotContain("object_id Nullable(String)",
                "AggregatingMergeTree refuses a nullable sorting key");
    }

    [Fact]
    public void ListingEventTypeAsADimension_RefusesTheBoot()
    {
        RollupOptions options = Valid();
        options.Dimensions = "event_type";

        Action build = () => Build(options);
        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*every rollup already keys on*");
    }

    [Fact]
    public void AnUndeclaredMeasure_RefusesTheBoot()
    {
        RollupOptions options = Valid();
        options.Measures = "nope:sum";

        Action build = () => Build(options);
        build.Should().Throw<InvalidOperationException>().WithMessage("*not an active declared measure*");
    }

    [Fact]
    public void AnAggregationTheMeasureDoesNotDeclare_RefusesTheBoot()
    {
        RollupOptions options = Valid();
        options.Measures = "price:avg";

        Action build = () => Build(options);
        build.Should().Throw<InvalidOperationException>().WithMessage("*does not declare it*");
    }

    [Fact]
    public void APercentile_IsRefused_BecauseARollupCannotStoreIt()
    {
        RollupOptions options = Valid();
        options.Measures = "dwell_ms:p95";

        Action build = () => Build(options);
        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*a rollup cannot store*Percentiles stay on the raw table*");
    }

    [Fact]
    public void DuplicateName_RefusesTheBoot()
    {
        Action build = () => Build(Valid(), Valid());
        build.Should().Throw<InvalidOperationException>().WithMessage("*a second time*");
    }

    [Fact]
    public void TableDdl_KeepsSortingKeyColumnsNonNullable()
    {
        string ddl = RollupRegistry.BuildTableDdl(Build(Valid()).Declared.Single());

        ddl.Should().Contain("widget_id String").And.Contain("shelf LowCardinality(String)");
        ddl.Should().NotContain("widget_id Nullable(String)",
            "AggregatingMergeTree refuses a nullable sorting key — 'Sorting key contains nullable "
            + "columns, but merge tree setting allow_nullable_key is disabled'. The source dimension "
            + "is Nullable(String); the rollup stores the normalised key instead.");
        ddl.Should().NotContain("shelf Nullable");
    }

    [Fact]
    public void TableDdl_PartitionsByMonthAndSortsByTheGroupingColumns()
    {
        string ddl = RollupRegistry.BuildTableDdl(Build(Valid()).Declared.Single());

        ddl.Should().Contain("ENGINE = AggregatingMergeTree");
        ddl.Should().Contain("PARTITION BY toYYYYMM(bucket)");
        ddl.Should().Contain("ORDER BY (project_id, tenant_id, bucket, event_type, widget_id, shelf)");
    }

    [Fact]
    public void TableDdl_StoresTheThreeCountGrainsAndEveryDeclaredMeasure()
    {
        string ddl = RollupRegistry.BuildTableDdl(Build(Valid()).Declared.Single());

        ddl.Should().Contain("events AggregateFunction(count)");
        ddl.Should().Contain("sessions AggregateFunction(uniqExact, String)");
        ddl.Should().Contain("users AggregateFunction(uniqExact, Nullable(String))");
        ddl.Should().Contain("dwell_ms_sum AggregateFunction(sum, Nullable(UInt32))");
        ddl.Should().Contain("price_sum AggregateFunction(sum, Nullable(Decimal(18, 4)))",
            "these strings are compared against system.columns verbatim, and Decimal renders with a "
            + "space after the comma");
    }

    [Fact]
    public void ViewDdl_NormalisesKeysExactlyAsBreakdownDoes()
    {
        string ddl = RollupRegistry.BuildViewDdl(Build(Valid()).Declared.Single());

        ddl.Should().Contain("ifNull(toString(widget_id), '') AS widget_id",
            "breakdown renders its key the same way, so a rollup that normalised differently would "
            + "answer the same question with different keys");
    }

    [Fact]
    public void ViewDdl_RestrictsToTheDeclaredEventTypes()
    {
        string ddl = RollupRegistry.BuildViewDdl(Build(Valid()).Declared.Single());

        ddl.Should().Contain("WHERE event_type IN ('impression', 'view')");
    }

    [Fact]
    public void ViewDdl_WithoutEventTypes_AggregatesEverything()
    {
        RollupOptions options = Valid();
        options.EventTypes = "";

        string ddl = RollupRegistry.BuildViewDdl(Build(options).Declared.Single());

        ddl.Should().NotContain("WHERE event_type IN");
    }

    [Fact]
    public void ARollupRestrictedToEventTypes_OnlyCoversThose()
    {
        Rollup rollup = Build(Valid()).Declared.Single();

        rollup.CoversEventType("view").Should().BeTrue();
        rollup.CoversEventType("other").Should().BeFalse();
        rollup.CoversEventType(null).Should().BeFalse(
            "a request with no event type spans everything, which a restricted rollup would undercount");
    }

    [Fact]
    public void AnUnrestrictedRollup_CoversAnyEventType()
    {
        RollupOptions options = Valid();
        options.EventTypes = "";

        Rollup rollup = Build(options).Declared.Single();

        rollup.CoversEventType(null).Should().BeTrue();
        rollup.CoversEventType("anything").Should().BeTrue();
    }

    [Fact]
    public void MeasureColumn_ResolvesOnlyDeclaredPairs()
    {
        Rollup rollup = Build(Valid()).Declared.Single();

        rollup.MeasureColumn("dwell_ms", "sum").Should().Be("dwell_ms_sum");
        rollup.MeasureColumn("dwell_ms", "avg").Should().BeNull("the rollup stores sum only");
        rollup.MeasureColumn("nope", "sum").Should().BeNull();
    }
}

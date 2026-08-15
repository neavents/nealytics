using FluentAssertions;
using Nealytics.Engine.Features.GetSchema;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// <c>/schema</c> reports which declared columns are actually carrying data.
///
/// <b>Why this exists.</b> A dimension that is declared, reconciled into ClickHouse, offered by the
/// query allowlist and returned by <c>/schema</c> can still be empty on every row, because nothing
/// upstream sends it. Every layer reports success; the column is simply blank. That is
/// indistinguishable, from the outside, from a venue that had no traffic — and only one of those is
/// a bug.
///
/// It is not a hypothetical. Four dimensions in this estate's own declaration — <c>locale</c>,
/// <c>translation_present</c>, <c>query_id</c> and <c>has_photo</c> — were declared, created, and
/// empty on all 4,542 rows for the entire retention window, because the producer that fills them
/// was written but never deployed. Finding that took hand-written ClickHouse SQL against the
/// container. It should take one authenticated GET, which is what this endpoint is for.
///
/// The same shape burned this estate before at the other end: <c>menu_id</c> was populated for
/// months with the packed payload's dense id, so a leaderboard built on it rendered two healthy
/// bars labelled "0" and "01MENU". Population is not proof of correctness — but absence of
/// population is proof of a gap, and that half is cheap to report.
/// </summary>
public class ColumnPopulationTests
{
    private static (DimensionRegistry Dimensions, MeasureRegistry Measures) Build()
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions =
            [
                new DimensionOptions { Name = "widget_id", Type = "String" },
                new DimensionOptions { Name = "locale", Type = "LowCardinality" },
                new DimensionOptions { Name = "bucket", Type = "UInt64" },
                new DimensionOptions { Name = "seen_at", Type = "DateTime" },
                new DimensionOptions { Name = "gone_id", Type = "String", Retired = true },
            ],
            Measures =
            [
                new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "sum,avg" },
                new MeasureOptions { Name = "old_ms", Type = "UInt32", Retired = true },
            ],
        };

        DimensionRegistry dimensions = new(options);
        return (dimensions, new MeasureRegistry(options, dimensions));
    }

    private static string Sql()
    {
        (DimensionRegistry dimensions, MeasureRegistry measures) = Build();
        return new GetColumnPopulationQuery(null!, dimensions, measures).BuildSql();
    }

    [Fact]
    public void AStringDimensionCountsNonEmptyRatherThanNonNull()
    {
        // A String dimension is Nullable(String), and the sanitizer writes a dropped or absent
        // value as NULL -- but a producer that sends the key with an empty value writes ''. Both
        // mean "nothing was collected", and count() would report the second as data.
        GetColumnPopulationQuery.CountExpression("widget_id", stringLike: true)
            .Should().Be("countIf(coalesce(widget_id, '') != '') AS widget_id");
    }

    [Fact]
    public void ALowCardinalityDimensionIsTreatedAsStringLike()
    {
        // LowCardinality(String) is NOT nullable -- it defaults to ''. So an unfilled row holds an
        // empty string, and count() over it would return every row in the table as populated,
        // which is precisely backwards.
        (DimensionRegistry dimensions, _) = Build();
        Dimension locale = dimensions.Active.First(d => d.Name == "locale");

        GetColumnPopulationQuery.IsStringLike(locale.Kind).Should().BeTrue();
        Sql().Should().Contain("countIf(coalesce(locale, '') != '') AS locale");
    }

    [Fact]
    public void ANumericOrDateDimensionCountsNonNull()
    {
        // Nullable(UInt64) has no empty-string state, and 0 is a legitimate value. count() over a
        // nullable column counts non-nulls, which is exactly the question.
        Sql().Should().Contain("count(bucket) AS bucket");
        Sql().Should().Contain("count(seen_at) AS seen_at");
    }

    [Fact]
    public void EveryMeasureIsCountedAsNonNull()
    {
        // Measures are Nullable by declaration so that avg skips absent values rather than
        // averaging in a zero. That makes non-null the right census for them too.
        Sql().Should().Contain("count(dwell_ms) AS dwell_ms");
    }

    [Fact]
    public void RetiredColumnsAreNotCensused()
    {
        string sql = Sql();

        sql.Should().NotContain("gone_id", "a retired dimension keeps its column and its rows, but "
            + "the query API stops offering it -- reporting its population would invite a caller "
            + "to build on something deliberately withdrawn");
        sql.Should().NotContain("old_ms");
    }

    [Fact]
    public void TheTenantPredicateIsBoundNeverInterpolated()
    {
        string sql = Sql();

        sql.Should().Contain("project_id = {projectId:String}");
        sql.Should().Contain("tenant_id = {tenantId:String}");
        sql.Should().Contain("timestamp >= {fromTimestamp:DateTime64}");
    }

    [Fact]
    public void ColumnNamesComeFromTheRegistryAndNeverFromACaller()
    {
        // The injection boundary is the same one BreakdownColumns carries: the only strings that
        // reach this SQL are names the registry validated against ^[a-z][a-z0-9_]{0,62}$ at boot.
        // There is no caller input on this path at all -- the endpoint takes no column parameter.
        //
        // Removing the empty-string literals the coalesce compares against must leave a statement
        // with no quotes at all. Asserting "contains no quote" outright would be wrong, and a test
        // that is wrong in the safe direction still has to be rewritten before it means anything.
        Sql().Replace("''", string.Empty, StringComparison.Ordinal)
            .Should().NotContain("'", "every remaining literal would be interpolated text");
    }

    [Fact]
    public void ADeploymentDeclaringNothingProducesNoQueryRatherThanInvalidSql()
    {
        // SELECT  FROM ... is a syntax error, and an engine with no declared dimensions is the
        // out-of-the-box state every new deployment starts in.
        TelemetryEngineOptions empty = new();
        DimensionRegistry dimensions = new(empty);
        MeasureRegistry measures = new(empty, dimensions);

        new GetColumnPopulationQuery(null!, dimensions, measures).BuildSql().Should().BeEmpty();
    }

    [Fact]
    public void OneRoundTripCoversEveryColumn()
    {
        // One row of counts, not one query per column. A 24-entry declaration would otherwise be 24
        // scans on every dashboard load, which is the cost pattern the rollups exist to avoid.
        Sql().Split("FROM").Should().HaveCount(2);
        Sql().Should().StartWith("SELECT ");
    }
}

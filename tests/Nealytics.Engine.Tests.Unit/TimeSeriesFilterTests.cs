using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Features.GetEventTimeSeries;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// Scoping a time series to one entity.
///
/// <b>What could not be asked before this.</b> <c>/timeseries</c> took no filters at all, so a
/// series could only ever cover a whole tenant. "Views over time for THIS article" was not
/// expressible — which meant a per-article, per-section or per-item chart either showed the tenant's
/// total under an entity's heading, or could not be built. Every scoped analytics page in a
/// dashboard depends on this one parameter.
///
/// The parser is shared with <c>/breakdown</c> rather than reimplemented, so the two endpoints
/// cannot drift about what a filter means — and the one that decides which rows are counted is not
/// the one anybody reads.
/// </summary>
public class TimeSeriesFilterTests
{
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    private static QueryColumns Columns() =>
        new(new DimensionRegistry(new TelemetryEngineOptions
        {
            Dimensions =
            [
                new DimensionOptions { Name = "article_id", Type = "String" },
                new DimensionOptions { Name = "section_id", Type = "String" },
            ],
        }));

    private static MeasureRegistry Measures()
    {
        TelemetryEngineOptions options = new();
        return new MeasureRegistry(options, new DimensionRegistry(options));
    }

    private static EventTimeSeriesRequestResult Create(params string[] filters) =>
        EventTimeSeriesRequestFactory.Create(
            "proj", "tenant", null, "day", null, null, null, null, null, null,
            filters, Columns(), Measures(), 10_000, 24, Now);

    [Fact]
    public void AFilterIsResolvedToItsColumn()
    {
        EventTimeSeriesRequestResult result = Create("article_id:01ARZ3NDEKTSV4RRFFQ69G5FAV");

        result.Success.Should().BeTrue();
        result.Request.Filters.Should().ContainSingle();
        result.Request.Filters[0].Column.Should().Be("article_id");
        result.Request.Filters[0].Value.Should().Be("01ARZ3NDEKTSV4RRFFQ69G5FAV");
    }

    [Fact]
    public void FiltersCompose()
    {
        // A section page scopes by article AND section. If the second overwrote the first the chart
        // would silently widen to the whole article and still look plausible.
        EventTimeSeriesRequestResult result = Create("article_id:01ARTICLE", "section_id:01SECTION");

        result.Request.Filters.Should().HaveCount(2);
    }

    [Fact]
    public void NoFiltersIsStillAValidRequest()
    {
        Create().Success.Should().BeTrue("the tenant-wide series is the original behaviour");
    }

    [Fact]
    public void AnUnknownColumnIsRefused()
    {
        EventTimeSeriesRequestResult result = Create("no_such_column:x");

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(400);
    }

    [Fact]
    public void AMalformedFilterIsRefused()
    {
        Create("article_id-no-colon").Success.Should().BeFalse();
    }

    [Fact]
    public void AValueMayContainAColon()
    {
        // Split on the FIRST colon only: a page path or a timestamp legitimately contains one.
        EventTimeSeriesRequestResult result = Create("article_id:a:b:c");

        result.Success.Should().BeTrue();
        result.Request.Filters[0].Value.Should().Be("a:b:c");
    }

    [Fact]
    public void TheColumnReachingSqlIsNeverTheCallersString()
    {
        // The injection boundary. This does not resolve, so it is a 400 and the SQL builder never
        // sees it — the same property /breakdown relies on.
        Create("article_id; DROP TABLE global_events--:x").Success.Should().BeFalse();
    }

    [Fact]
    public void TheFilterReachesTheSqlAsABoundParameter()
    {
        EventTimeSeriesRequestResult result = Create("article_id:01ARTICLE");

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) =
            GetEventTimeSeriesQuery.BuildQuery(result.Request);

        sql.Should().Contain("AND toString(article_id) = {filter0:String}");
        sql.Should().NotContain("01ARTICLE", "the value is bound, never concatenated");
        parameters.Should().Contain(p => p.Key == "filter0" && (string?)p.Value == "01ARTICLE");
    }

    [Fact]
    public void TwoFiltersBecomeTwoConditionsAndTwoParameters()
    {
        EventTimeSeriesRequestResult result = Create("article_id:01ARTICLE", "section_id:01SEC");

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) =
            GetEventTimeSeriesQuery.BuildQuery(result.Request);

        sql.Should().Contain("{filter0:String}");
        sql.Should().Contain("{filter1:String}");
        parameters.Count(p => p.Key.StartsWith("filter", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public void AnUnfilteredSeriesEmitsNoFilterClause()
    {
        (string sql, _) = GetEventTimeSeriesQuery.BuildQuery(Create().Request);

        sql.Should().NotContain("filter0");
    }
}

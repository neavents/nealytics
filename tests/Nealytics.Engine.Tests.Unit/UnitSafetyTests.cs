using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetDistribution;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public class UnitSafetyTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static TelemetryEngineOptions Options() => new()
    {
        Dimensions = [new DimensionOptions { Name = "currency", Type = "LowCardinality" }, new DimensionOptions { Name = "widget_id" }],
        Measures =
        [
            new MeasureOptions { Name = "amount", Type = "Decimal", Aggregations = "sum,avg,count,p95", UnitDimension = "currency" },
            new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "sum" },
        ],
    };

    private static (QueryColumns Columns, MeasureRegistry Measures) Registries()
    {
        TelemetryEngineOptions options = Options();
        DimensionRegistry dimensions = new(options);
        return (new QueryColumns(dimensions), new MeasureRegistry(options, dimensions));
    }

    private static BreakdownRequestResult Breakdown(string metric, string groupBy, params string[] filters)
    {
        (QueryColumns columns, MeasureRegistry measures) = Registries();
        return BreakdownRequestFactory.Create(
            "p", "t", metric, groupBy, null, filters, null, null, null, null, null, null, null, columns, measures, 100, 24, Now);
    }

    [Fact]
    public void SummingAcrossUnits_IsRefused()
    {
        BreakdownRequestResult result = Breakdown("sum(amount)", "widget_id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("units of 'currency'").And.Contain("filter=currency:");
    }

    [Fact]
    public void GroupingByTheUnit_IsAllowed()
    {
        Breakdown("sum(amount)", "currency").Success.Should().BeTrue();
    }

    [Fact]
    public void FilteringToOneUnit_IsAllowed()
    {
        Breakdown("avg(amount)", "widget_id", "currency:EUR").Success.Should().BeTrue();
    }

    [Fact]
    public void CountingIsUnitFree()
    {
        Breakdown("count(amount)", "widget_id").Success.Should().BeTrue();
    }

    [Fact]
    public void AMeasureWithoutAUnit_IsUnaffected()
    {
        Breakdown("sum(dwell_ms)", "widget_id").Success.Should().BeTrue();
    }

    [Fact]
    public void APivotMetricAcrossUnits_IsRefused()
    {
        (QueryColumns columns, MeasureRegistry measures) = Registries();

        PivotRequestResult refused = PivotRequestFactory.Create(
            "p", "t", "widget_id", ["events", "sum(amount)"], [], null, null, null, null, null, null, null, null,
            columns, measures, 100, 24, Now);
        PivotRequestResult grouped = PivotRequestFactory.Create(
            "p", "t", "currency", ["events", "sum(amount)"], [], null, null, null, null, null, null, null, null,
            columns, measures, 100, 24, Now);

        refused.Success.Should().BeFalse();
        refused.ErrorMessage.Should().Contain("units of 'currency'");
        grouped.Success.Should().BeTrue();
    }

    [Fact]
    public void ADistributionAcrossUnits_IsRefused_UntilFilteredToOne()
    {
        (QueryColumns columns, MeasureRegistry measures) = Registries();

        DistributionRequestFactory.Create("p", "t", "amount", null, [], null, null, null, null, null, null, columns, measures, 24, Now)
            .Success.Should().BeFalse();
        DistributionRequestFactory.Create("p", "t", "amount", null, ["currency:TRY"], null, null, null, null, null, null, columns, measures, 24, Now)
            .Success.Should().BeTrue();
    }

    [Fact]
    public void AUnitThatIsNotADeclaredDimension_RefusesTheBoot()
    {
        TelemetryEngineOptions options = Options();
        options.Measures[0].UnitDimension = "currency_code";

        Action act = () => new MeasureRegistry(options, new DimensionRegistry(options));
        act.Should().Throw<InvalidOperationException>().WithMessage("*UnitDimension 'currency_code'*");
    }
}

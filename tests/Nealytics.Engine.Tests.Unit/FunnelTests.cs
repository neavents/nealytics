using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetFunnel;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public class FunnelTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    private static BreakdownColumns Columns() =>
        new(new DimensionRegistry(new TelemetryEngineOptions
        {
            Dimensions = [new DimensionOptions { Name = "locale", Type = "LowCardinality" }],
        }));

    private static MeasureRegistry Measures()
    {
        TelemetryEngineOptions options = new()
        {
            Measures = [new MeasureOptions { Name = "dwell_ms", Type = "UInt32" }],
        };
        return new MeasureRegistry(options, new DimensionRegistry(options));
    }

    private static FunnelRequestResult Create(
        string[]? steps = null, string? grain = null, string? breakdownBy = null,
        string? window = null, string? limit = null)
        => FunnelRequestFactory.Create(
            "proj", "tenant", steps ?? ["open", "view", "order"], grain, breakdownBy, window,
            null, null, limit, Columns(), Measures(), 10_000, 24, Now);

    [Fact]
    public void FewerThanTwoSteps_IsRefused()
    {
        Create(steps: ["open"]).Success.Should().BeFalse("one step is a count, not a funnel");
        Create(steps: []).Success.Should().BeFalse();
    }

    [Fact]
    public void MoreStepsThanTheCap_IsRefused()
    {
        string[] many = [.. Enumerable.Range(0, FunnelRequestFactory.MaxSteps + 1).Select(i => $"e{i}")];

        Create(steps: many).Success.Should().BeFalse();
    }

    [Fact]
    public void WindowFunnelReceivesADateTime_NotADateTime64()
    {
        (string sql, _) = GetFunnelQuery.BuildQuery(Create().Request);

        sql.Should().Contain("windowFunnel(1800)(toDateTime(timestamp)",
            "ClickHouse refuses DateTime64 as windowFunnel's first argument with ILLEGAL_TYPE_OF_ARGUMENT");
    }

    [Fact]
    public void EventTypesReachTheSqlAsParameters_NeverInline()
    {
        (string sql, var parameters) = GetFunnelQuery.BuildQuery(Create().Request);

        sql.Should().NotContain("'open'").And.NotContain("'order'");
        parameters.Should().Contain(p => p.Key == "step0Type" && (string)p.Value! == "open");
        parameters.Should().Contain(p => p.Key == "step2Type" && (string)p.Value! == "order");
    }

    [Fact]
    public void TheScanIsNarrowedToTheStepEventTypes()
    {
        (string sql, _) = GetFunnelQuery.BuildQuery(Create().Request);

        sql.Should().Contain("AND event_type IN ({step0Type:String}, {step1Type:String}, {step2Type:String})",
            "without this the funnel reads every event in range to answer a three-event question");
    }

    [Fact]
    public void SessionsGrain_GroupsBySession()
    {
        (string sql, _) = GetFunnelQuery.BuildQuery(Create().Request);

        sql.Should().Contain("GROUP BY session_id");
        sql.Should().NotContain("user_id IS NOT NULL");
    }

    [Fact]
    public void UsersGrain_GroupsByUser_AndExcludesAnonymous()
    {
        (string sql, _) = GetFunnelQuery.BuildQuery(Create(grain: "users").Request);

        sql.Should().Contain("GROUP BY user_id");
        sql.Should().Contain("AND user_id IS NOT NULL",
            "anonymous rows carry a NULL user_id and would otherwise collapse into one phantom user");
    }

    [Fact]
    public void UnknownGrain_Is400()
    {
        Create(grain: "devices").Success.Should().BeFalse();
    }

    [Fact]
    public void AStepFilter_ResolvesThroughTheAllowlist()
    {
        FunnelRequestResult result = Create(steps: ["open", "view:locale=tr"]);

        result.Success.Should().BeTrue();
        result.Request.Steps[1].FilterColumn.Should().Be("locale");
        result.Request.Steps[1].FilterValue.Should().Be("tr");

        (string sql, var parameters) = GetFunnelQuery.BuildQuery(result.Request);
        sql.Should().Contain("toString(locale) = {step1Value:String}");
        parameters.Should().Contain(p => p.Key == "step1Value" && (string)p.Value! == "tr");
    }

    [Theory]
    [InlineData("view:nope=tr")]
    [InlineData("view:metadata_json=x")]
    [InlineData("view:locale; DROP TABLE global_events=x")]
    public void AnUnknownStepFilterColumn_Is400(string step)
    {
        Create(steps: ["open", step]).Success.Should().BeFalse();
    }

    [Fact]
    public void AMeasureAsAStepFilter_Is400ExplainingTheKind()
    {
        FunnelRequestResult result = Create(steps: ["open", "view:dwell_ms=100"]);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("is a declared measure, not a dimension");
    }

    [Fact]
    public void BreakdownBy_SegmentsAndCapsTheGroups()
    {
        FunnelRequestResult result = Create(breakdownBy: "locale", limit: "5");

        result.Success.Should().BeTrue();

        (string sql, _) = GetFunnelQuery.BuildQuery(result.Request);
        sql.Should().Contain("ifNull(toString(locale), '') AS segment");
        sql.Should().Contain("GROUP BY segment ORDER BY s0 DESC LIMIT {limit:Int32}");
    }

    [Fact]
    public void BreakdownByAMeasure_Is400()
    {
        Create(breakdownBy: "dwell_ms").Success.Should().BeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("86401")]
    [InlineData("abc")]
    public void AnOutOfRangeWindow_Is400(string window)
    {
        Create(window: window).Success.Should().BeFalse();
    }

    [Fact]
    public void ADeclaredWindow_ReachesTheAggregate()
    {
        (string sql, _) = GetFunnelQuery.BuildQuery(Create(window: "600").Request);

        sql.Should().Contain("windowFunnel(600)");
    }
}

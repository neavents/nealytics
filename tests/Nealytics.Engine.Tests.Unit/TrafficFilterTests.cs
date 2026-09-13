using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// Which traffic a read counts.
///
/// Bots were labelled at the edge from the day device_class existed and nothing ever filtered on
/// the label, so every number the dashboard has shown includes preview crawlers and link
/// unfurlers. The numbers dropping when this ships is the fix working.
/// </summary>
public class TrafficFilterTests
{
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    private static BreakdownRequestResult Create(string? traffic) =>
        BreakdownRequestFactory.Create(
            "proj", "tenant", "events", "event_type", null, [], null, null, null, null, traffic, null, null,
            new QueryColumns(new DimensionRegistry(new TelemetryEngineOptions())),
            new MeasureRegistry(new TelemetryEngineOptions(), new DimensionRegistry(new TelemetryEngineOptions())),
            10_000, 24, Now);

    [Fact]
    public void TheDefaultIsNormalTraffic()
    {
        TrafficFilter.TryParse(null, out string? value).Should().BeTrue();
        value.Should().Be("normal");
    }

    [Fact]
    public void AllMeansNoFilterAtAll()
    {
        TrafficFilter.TryParse("all", out string? value).Should().BeTrue();
        value.Should().BeNull();
        TrafficFilter.Clause(value).Should().BeEmpty(
            "an owner disputing a number can only be answered by looking at what was excluded");
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("bot")]
    [InlineData("internal")]
    public void EachKnownClassIsSelectable(string requested)
    {
        TrafficFilter.TryParse(requested, out string? value).Should().BeTrue();
        value.Should().Be(requested);
    }

    [Theory]
    [InlineData("Normal")]
    [InlineData("robots")]
    [InlineData("normal'; DROP TABLE global_events")]
    public void AnUnknownClassIsRefused(string requested)
    {
        TrafficFilter.TryParse(requested, out _).Should().BeFalse();
        TrafficFilter.Rejection(requested).Should().Contain(requested);
    }

    [Fact]
    public void TheResolvedValueIsTheStoredInstance_NotTheCallersString()
    {
        string callerSupplied = new(['b', 'o', 't']);

        TrafficFilter.TryParse(callerSupplied, out string? value).Should().BeTrue();

        value.Should().Be(callerSupplied);
        ReferenceEquals(value, callerSupplied).Should().BeFalse(
            "it reaches SQL as a bound parameter either way, but the allowlist's own instance is a "
            + "stronger property than 'we checked it'");
    }

    [Fact]
    public void ARequestWithNoTrafficParameter_ExcludesBots()
    {
        BreakdownRequestResult result = Create(null);

        result.Success.Should().BeTrue();
        result.Request.TrafficClass.Should().Be("normal");

        (string sql, var parameters) = GetBreakdownQuery.BuildQuery(result.Request);

        sql.Should().Contain("AND traffic_class = {trafficClass:String}");
        parameters.Should().ContainSingle(p => p.Key == "trafficClass" && (string)p.Value! == "normal");
    }

    [Fact]
    public void TrafficAll_EmitsNoClauseAndNoParameter()
    {
        BreakdownRequestResult result = Create("all");

        (string sql, var parameters) = GetBreakdownQuery.BuildQuery(result.Request);

        sql.Should().NotContain("traffic_class");
        parameters.Should().NotContain(p => p.Key == "trafficClass");
    }

    [Fact]
    public void AnUnknownTrafficValue_Is400NamingIt()
    {
        BreakdownRequestResult result = Create("robots");

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(400);
        result.ErrorMessage.Should().Contain("robots").And.Contain("normal, bot, internal, all");
    }
}

/// <summary>
/// Exact counting, opted into.
///
/// The table is a ReplacingMergeTree and no query uses FINAL, so a WAL replay after a restart can
/// leave the same event present twice until a background merge collapses it. The default is fast
/// and eventually right; this is slower and exact.
/// </summary>
public class ExactCountingTests
{
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    private static BreakdownRequestResult Create(string? exact) =>
        BreakdownRequestFactory.Create(
            "proj", "tenant", "events", "event_type", null, [], null, null, null, null, null, exact, null,
            new QueryColumns(new DimensionRegistry(new TelemetryEngineOptions())),
            new MeasureRegistry(new TelemetryEngineOptions(), new DimensionRegistry(new TelemetryEngineOptions())),
            10_000, 24, Now);

    [Fact]
    public void TheDefaultCountsRows()
    {
        (string sql, _) = GetBreakdownQuery.BuildQuery(Create(null).Request);

        sql.Should().Contain("count() AS value");
        sql.Should().NotContain("uniqExact(event_id)");
    }

    [Fact]
    public void ExactCountsDistinctEventIds()
    {
        (string sql, _) = GetBreakdownQuery.BuildQuery(Create("true").Request);

        sql.Should().Contain("uniqExact(event_id) AS value",
            "a replayed batch is present twice until a merge collapses it, and no query uses FINAL");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("True")]
    public void AnythingButTrueKeepsTheFastPath(string exact)
    {
        (string sql, _) = GetBreakdownQuery.BuildQuery(Create(exact).Request);

        sql.Should().Contain("count() AS value",
            "an exactness flag that turns on by accident makes every dashboard slower for no reason");
    }
}

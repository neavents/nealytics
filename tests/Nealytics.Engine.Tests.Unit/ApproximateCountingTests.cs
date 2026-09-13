using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// Counting distinct sessions and users without holding every one of them in memory.
///
/// <b>What breaks without this.</b> <c>uniqExact</c> builds a real hash set of every distinct value
/// it sees. Session ids are unique per visit, so that set grows with traffic, and a wide range over
/// a large estate does not get slower — it meets ClickHouse's memory limit and fails the whole
/// query. <c>uniq</c> is HyperLogLog: fixed, small memory whatever the cardinality, for roughly
/// 1.6% error.
///
/// <b>Opt-in, so no existing number moves.</b> A venue looking at a week of its own traffic should
/// keep getting the exact answer. The switch exists for the range that would otherwise return
/// nothing at all, and an estate that silently changed its numbers on upgrade would be worse than
/// one that failed.
///
/// The spelling matches <c>/active</c>'s <c>mode=exact|approx</c>, which has had this since it was
/// written. Two endpoints with different words for the same idea is its own defect.
/// </summary>
public class ApproximateCountingTests
{
    private static BreakdownRequest Request(BreakdownMetric metric, bool approximate) => new()
    {
        ProjectId = "p",
        TenantId = "t",
        Metric = metric,
        GroupByColumn = "event_type",
        MetricWire = "events",
        Filters = [],
        From = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        To = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc),
        Limit = 100,
        Approximate = approximate,
    };

    [Theory]
    [InlineData(BreakdownMetric.Sessions, "session_id")]
    [InlineData(BreakdownMetric.Users, "user_id")]
    public void ByDefaultTheCountIsExact(BreakdownMetric metric, string column)
    {
        (string sql, _) = GetBreakdownQuery.BuildQuery(Request(metric, approximate: false), null);

        sql.Should().Contain($"uniqExact({column})");
        sql.Should().NotContain($"uniq({column})");
    }

    [Theory]
    [InlineData(BreakdownMetric.Sessions, "session_id")]
    [InlineData(BreakdownMetric.Users, "user_id")]
    public void ApproximateSwitchesToHyperLogLog(BreakdownMetric metric, string column)
    {
        (string sql, _) = GetBreakdownQuery.BuildQuery(Request(metric, approximate: true), null);

        sql.Should().Contain($"uniq({column})");
        sql.Should().NotContain($"uniqExact({column})");
    }

    [Fact]
    public void EventCountingIsUnaffected()
    {
        // `events` counts rows, which is already constant-memory. Approximating it would trade
        // accuracy for nothing.
        (string sql, _) = GetBreakdownQuery.BuildQuery(Request(BreakdownMetric.Events, approximate: true), null);

        sql.Should().Contain("count()");
        sql.Should().NotContain("uniq(");
    }

    [Theory]
    [InlineData("approx", true)]
    [InlineData("exact", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Approx", false)]
    [InlineData("approximate", false)]
    public void OnlyTheLiteralApproxOptsIn(string? raw, bool expected)
    {
        // Strict, and matching how `exact` is parsed next door. A near miss must do nothing rather
        // than quietly change what a number means — the failure mode of a silently approximate
        // count is a figure somebody disputes and nobody can reproduce.
        BreakdownRequestResult result = BreakdownRequestFactory.Create(
            "p", "t", "sessions", "event_type", null, [], null, null, null, null, null, null, raw,
            new QueryColumns(new Nealytics.Engine.Infrastructure.Configuration.DimensionRegistry(
                new Nealytics.Engine.Infrastructure.Configuration.TelemetryEngineOptions())),
            new Nealytics.Engine.Infrastructure.Configuration.MeasureRegistry(
                new Nealytics.Engine.Infrastructure.Configuration.TelemetryEngineOptions(),
                new Nealytics.Engine.Infrastructure.Configuration.DimensionRegistry(
                    new Nealytics.Engine.Infrastructure.Configuration.TelemetryEngineOptions())),
            10_000, 24, new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc));

        result.Request.Approximate.Should().Be(expected);
    }
}

using FluentAssertions;
using Nealytics.Engine.Features.GetFunnel;
using Nealytics.Engine.Features.GetProjectTimeline;
using Nealytics.Engine.Features.GetRetention;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public class RetentionAndStitchingTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static (QueryColumns Columns, MeasureRegistry Measures) Registries()
    {
        TelemetryEngineOptions options = new();
        DimensionRegistry dimensions = new(options);
        return (new QueryColumns(dimensions), new MeasureRegistry(options, dimensions));
    }

    private static RetentionRequestResult Retention(
        string? period = null, string? by = null, string? from = null, string? to = null, string? alias = null)
    {
        (QueryColumns columns, MeasureRegistry measures) = Registries();
        return RetentionRequestFactory.Create("p", "t", period, by, null, [], from, to, null, columns, measures, alias, Now);
    }

    [Fact]
    public void Defaults_AreWeeklyUserRetentionOverEightWeeks()
    {
        RetentionRequest request = Retention().Request;

        request.Period.Should().Be(RetentionPeriod.Week);
        request.Actor.Should().Be(RetentionActor.Users);
        request.PeriodCount.Should().Be(8);
        request.From.Should().Be(new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc), "eight Monday-starting weeks end with the one holding 'to'");
    }

    [Theory]
    [InlineData("day", "2026-09-01", "2026-09-30", 30)]
    [InlineData("week", "2026-09-01", "2026-09-30", 5)]
    [InlineData("month", "2026-01-15", "2026-09-30", 9)]
    public void PeriodsAreCountedInclusively(string period, string from, string to, int expected)
    {
        Retention(period, from: from, to: to).Request.PeriodCount.Should().Be(expected);
    }

    [Fact]
    public void ATooLongRange_IsRefused()
    {
        Retention("day", from: "2026-01-01", to: "2026-09-30").ErrorMessage.Should().Contain("at most 120");
    }

    [Theory]
    [InlineData("hour", null)]
    [InlineData(null, "devices")]
    public void UnknownValues_AreRefused(string? period, string? by)
    {
        Retention(period, by).Success.Should().BeFalse();
    }

    [Fact]
    public void StitchedRetention_NeedsAnAliasEventType()
    {
        Retention(by: "identities").ErrorMessage.Should().Contain("AliasEventType is not set");
        Retention(by: "identities", alias: "alias").Request.Actor.Should().Be(RetentionActor.Identities);
    }

    [Fact]
    public void RetentionSql_GroupsActorsByFirstSeenPeriod()
    {
        string sql = RetentionRequestFactory.Create("p", "t", "week", "users", "open", [], null, null, null,
            Registries().Columns, Registries().Measures, null, Now).Request is RetentionRequest request
            ? GetRetentionQuery.BuildQuery(request).Sql
            : string.Empty;

        sql.Should().StartWith("WITH activity AS (SELECT assumeNotNull(user_id) AS actor, toMonday(timestamp) AS period FROM nealytics_core.global_events WHERE project_id");
        sql.Should().Contain("AND event_type = {eventType:String}");
        sql.Should().Contain("AND user_id IS NOT NULL GROUP BY actor, period)");
        sql.Should().Contain("cohorts AS (SELECT actor, min(period) AS cohort FROM activity GROUP BY actor)");
        sql.Should().Contain("intDiv(dateDiff('day', cohorts.cohort, activity.period), 7) AS period_offset");
    }

    [Fact]
    public void StitchedRetention_ReadsFromTheStitchedSource()
    {
        RetentionRequest request = Retention(by: "identities", alias: "alias").Request;
        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = GetRetentionQuery.BuildQuery(request);

        sql.Should().Contain("stitched_id AS actor");
        sql.Should().Contain("LEFT ANY JOIN (SELECT session_id AS anonymous_id");
        parameters.Should().Contain(new KeyValuePair<string, object?>("aliasEventType", "alias"));
    }

    [Fact]
    public void WeekStart_IsMonday()
    {
        RetentionRequestFactory.Start(new DateTime(2026, 10, 4, 15, 0, 0, DateTimeKind.Utc), RetentionPeriod.Week)
            .Should().Be(new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc));
    }

    private static FunnelRequestResult Funnel(string grain, string? alias)
    {
        (QueryColumns columns, MeasureRegistry measures) = Registries();
        return FunnelRequestFactory.Create("p", "t", ["view", "order"], grain, null, null, null, null, null, columns, measures, 100, 24, Now, null, alias);
    }

    [Fact]
    public void AStitchedFunnel_NeedsAnAliasEventType()
    {
        Funnel("identities", null).ErrorMessage.Should().Contain("AliasEventType is not set");
        Funnel("identities", "alias").Request.Grain.Should().Be(FunnelGrain.Identities);
    }

    [Fact]
    public void AStitchedFunnel_CountsEachStitchedIdentityOnce()
    {
        string sql = GetFunnelQuery.BuildQuery(Funnel("identities", "alias").Request).Sql;

        sql.Should().Contain("FROM (SELECT *, multiIf(ifNull(user_id, '') != '', ifNull(user_id, ''), hop2.identity_id != ''");
        sql.Should().Contain("AS hop2 ON hop1.identity_id = hop2.anonymous_id WHERE project_id = {projectId:String}");
        sql.Should().Contain("GROUP BY stitched_id)");
    }

    [Fact]
    public void AnUnstitchedFunnel_BuildsTheSameSqlAsBefore()
    {
        string sql = GetFunnelQuery.BuildQuery(Funnel("sessions", "alias").Request).Sql;

        sql.Should().Contain("FROM nealytics_core.global_events WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String} AND timestamp >=");
        sql.Should().NotContain("stitched_id");
    }

    [Fact]
    public void TheTimeline_FiltersByUser()
    {
        TimelineRequestResult result = TimelineRequestFactory.Create("p", "t", null, null, null, null, null, null, null, 100, "u1");

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = GetProjectTimelineQuery.BuildQuery(result.Request);
        sql.Should().Contain(" AND user_id = {userId:String} ORDER BY");
        parameters.Should().Contain(new KeyValuePair<string, object?>("userId", "u1"));
    }

    [Fact]
    public void AStitchedTimeline_IncludesSessionsAliasedToTheUser()
    {
        TimelineRequestResult result = TimelineRequestFactory.Create("p", "t", null, null, null, null, null, null, null, 100, "u1", "true", "alias");

        string sql = GetProjectTimelineQuery.BuildQuery(result.Request).Sql;
        sql.Should().Contain("AND (user_id = {userId:String} OR session_id IN (WITH links AS (");
        sql.Should().Contain("INNER JOIN links AS hop2 ON hop1.identity_id = hop2.anonymous_id WHERE hop2.identity_id = {userId:String}");
    }

    [Fact]
    public void AStitchedTimeline_NeedsAUserAndStitching()
    {
        TimelineRequestFactory.Create("p", "t", null, null, null, null, null, null, null, 100, null, "true", "alias")
            .Success.Should().BeFalse();
        TimelineRequestFactory.Create("p", "t", null, null, null, null, null, null, null, 100, "u1", "true", null)
            .ErrorMessage.Should().Contain("AliasEventType is not set");
    }
}

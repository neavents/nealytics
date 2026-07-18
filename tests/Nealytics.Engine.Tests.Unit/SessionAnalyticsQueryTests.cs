using FluentAssertions;
using Nealytics.Engine.Features.GetSessionAnalytics;

namespace Nealytics.Engine.Tests.Unit;

public class SessionAnalyticsQueryTests
{
    [Fact]
    public void BuildQuery_ProducesGroupedAggregateSql_WithAllParameters()
    {
        SessionAnalyticsRequest request = new SessionAnalyticsRequest
        {
            ProjectId = "proj",
            TenantId = "tenant",
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Limit = 200
        };

        (string sql, var parameters) = GetSessionAnalyticsQuery.BuildQuery(request);

        sql.Should().Contain("GROUP BY session_id");
        sql.Should().Contain("min(timestamp) AS first_seen");
        sql.Should().Contain("max(timestamp) AS last_seen");
        sql.Should().Contain("count() AS event_count");
        sql.Should().Contain("WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}");

        parameters.Select(p => p.Key).Should()
            .BeEquivalentTo(new[] { "projectId", "tenantId", "fromTimestamp", "toTimestamp", "limit" });
        parameters.Should().ContainSingle(p => p.Key == "limit" && (int)p.Value! == 200);
    }

    [Fact]
    public void Aggregate_EmptySessions_ReturnsZeroedSummary()
    {
        SessionAnalyticsResponse response = GetSessionAnalyticsQuery.Aggregate(
            "proj", "tenant", new List<SessionSummaryItem>());

        response.UniqueSessionCount.Should().Be(0);
        response.TotalEventCount.Should().Be(0);
        response.AvgDurationSeconds.Should().Be(0);
        response.Sessions.Should().BeEmpty();
        response.ProjectId.Should().Be("proj");
        response.TenantId.Should().Be("tenant");
    }

    [Fact]
    public void Aggregate_MultipleSessions_SumsAndAverages()
    {
        List<SessionSummaryItem> sessions = new List<SessionSummaryItem>
        {
            new SessionSummaryItem { SessionId = "a", EventCount = 3, DurationSeconds = 10 },
            new SessionSummaryItem { SessionId = "b", EventCount = 7, DurationSeconds = 30 }
        };

        SessionAnalyticsResponse response = GetSessionAnalyticsQuery.Aggregate("p", "t", sessions);

        response.UniqueSessionCount.Should().Be(2);
        response.TotalEventCount.Should().Be(10);
        response.AvgDurationSeconds.Should().Be(20);
    }

    [Fact]
    public void Aggregate_LargeEventCounts_DoNotOverflow()
    {
        List<SessionSummaryItem> sessions = new List<SessionSummaryItem>
        {
            new SessionSummaryItem { SessionId = "a", EventCount = 3_000_000_000, DurationSeconds = 1 },
            new SessionSummaryItem { SessionId = "b", EventCount = 3_000_000_000, DurationSeconds = 1 }
        };

        SessionAnalyticsResponse response = GetSessionAnalyticsQuery.Aggregate("p", "t", sessions);

        response.TotalEventCount.Should().Be(6_000_000_000,
            "event counts are 64-bit and must not overflow past int.MaxValue");
    }
}

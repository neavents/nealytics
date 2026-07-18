using FluentAssertions;
using Nealytics.Engine.Features.GetProjectTimeline;

namespace Nealytics.Engine.Tests.Unit;

public class TimelineQueryBuilderTests
{
    private static TimelineQueryRequest BaseRequest() => new TimelineQueryRequest
    {
        ProjectId = "proj",
        TenantId = "tenant",
        Limit = 50
    };

    [Fact]
    public void BuildQuery_WithNoFilters_ProducesBaseQuery()
    {
        (string sql, var parameters) = GetProjectTimelineQuery.BuildQuery(BaseRequest());

        sql.Should().Contain("WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}");
        sql.Should().EndWith("ORDER BY timestamp DESC LIMIT {limit:Int32}");
        sql.Should().NotContain("event_type =");
        sql.Should().NotContain("session_id =");
        sql.Should().NotContain("item_id =");
        sql.Should().NotContain("timestamp <");

        parameters.Should().HaveCount(3);
        parameters.Should().ContainSingle(p => p.Key == "projectId" && (string)p.Value! == "proj");
        parameters.Should().ContainSingle(p => p.Key == "tenantId" && (string)p.Value! == "tenant");
        parameters.Should().ContainSingle(p => p.Key == "limit" && (int)p.Value! == 50);
    }

    [Fact]
    public void BuildQuery_WithCursor_AddsTimestampPredicateAndParameter()
    {
        DateTime cursor = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        TimelineQueryRequest request = new TimelineQueryRequest
        {
            ProjectId = "proj",
            TenantId = "tenant",
            Limit = 50,
            Before = cursor
        };

        (string sql, var parameters) = GetProjectTimelineQuery.BuildQuery(request);

        sql.Should().Contain("AND timestamp < {cursor:DateTime64}");
        parameters.Should().ContainSingle(p => p.Key == "cursor" && (DateTime)p.Value! == cursor);
    }

    [Fact]
    public void BuildQuery_WithEventTypeSessionAndItemFilters_AppendsAllPredicates()
    {
        TimelineQueryRequest request = new TimelineQueryRequest
        {
            ProjectId = "proj",
            TenantId = "tenant",
            Limit = 10,
            EventType = "click",
            SessionId = "sess-1",
            ItemId = "/home"
        };

        (string sql, var parameters) = GetProjectTimelineQuery.BuildQuery(request);

        sql.Should().Contain("AND event_type = {eventType:String}");
        sql.Should().Contain("AND session_id = {sessionId:String}");
        sql.Should().Contain("AND item_id = {itemId:String}");

        parameters.Should().ContainSingle(p => p.Key == "eventType" && (string)p.Value! == "click");
        parameters.Should().ContainSingle(p => p.Key == "sessionId" && (string)p.Value! == "sess-1");
        parameters.Should().ContainSingle(p => p.Key == "itemId" && (string)p.Value! == "/home");
        parameters.Should().HaveCount(6);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BuildQuery_WithEmptyOrNullFilter_IsIgnored(string? emptyValue)
    {
        TimelineQueryRequest request = new TimelineQueryRequest
        {
            ProjectId = "proj",
            TenantId = "tenant",
            Limit = 10,
            EventType = emptyValue,
            SessionId = emptyValue,
            ItemId = emptyValue
        };

        (string sql, var parameters) = GetProjectTimelineQuery.BuildQuery(request);

        sql.Should().NotContain("event_type =");
        sql.Should().NotContain("session_id =");
        sql.Should().NotContain("item_id =");
        parameters.Should().HaveCount(3);
    }

    [Fact]
    public void BuildQuery_ParameterOrder_MatchesClauseOrder()
    {
        TimelineQueryRequest request = new TimelineQueryRequest
        {
            ProjectId = "proj",
            TenantId = "tenant",
            Limit = 5,
            Before = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EventType = "view"
        };

        (_, var parameters) = GetProjectTimelineQuery.BuildQuery(request);

        parameters.Select(p => p.Key).Should()
            .ContainInOrder("projectId", "tenantId", "cursor", "eventType", "limit");
    }
}

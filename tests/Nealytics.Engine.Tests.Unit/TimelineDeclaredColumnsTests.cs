using FluentAssertions;
using Nealytics.Engine.Features.GetProjectTimeline;

namespace Nealytics.Engine.Tests.Unit;

public class TimelineDeclaredColumnsTests
{
    private static TimelineQueryRequest Request() =>
        new() { ProjectId = "proj", TenantId = "tenant", Limit = 10 };

    [Fact]
    public void WithoutDeclaredColumns_SelectsOnlyTheCoreSeven()
    {
        (string sql, _) = GetProjectTimelineQuery.BuildQuery(Request());

        sql.Should().Contain(
            "SELECT event_id, session_id, user_id, event_type, object_id, metadata_json, timestamp FROM");
    }

    [Fact]
    public void DeclaredColumns_AreSelectedAfterTheCoreSeven()
    {
        (string sql, _) = GetProjectTimelineQuery.BuildQuery(
            Request(), ["article_id", "table_id", "dwell_ms"]);

        sql.Should().Contain("timestamp, article_id, table_id, dwell_ms FROM",
            "a dimension value could not be read on a single event at all before, so no breakdown "
            + "row could be drilled into");
    }

    [Fact]
    public void CoreColumnCount_MatchesTheSelectList()
    {
        (string sql, _) = GetProjectTimelineQuery.BuildQuery(Request());

        string selectList = sql[("SELECT ".Length)..sql.IndexOf(" FROM", System.StringComparison.Ordinal)];

        selectList.Split(',').Length.Should().Be(
            GetProjectTimelineQuery.CoreColumnCount,
            "the reader indexes declared columns by offset from this constant, and an off-by-one "
            + "would put a measure's value under a dimension's name");
    }
}

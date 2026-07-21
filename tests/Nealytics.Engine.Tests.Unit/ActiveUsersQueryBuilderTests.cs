using FluentAssertions;
using Nealytics.Engine.Features.GetActiveUsers;

namespace Nealytics.Engine.Tests.Unit;

public class ActiveUsersQueryBuilderTests
{
    private static ActiveUsersRequest Request(
        ActiveUsersInterval interval = ActiveUsersInterval.Day,
        ActiveDimension dimension = ActiveDimension.User,
        ActiveCountMode mode = ActiveCountMode.Exact,
        int limit = 100)
        => new ActiveUsersRequest
        {
            ProjectId = "proj",
            TenantId = "tenant",
            From = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc),
            Interval = interval,
            Dimension = dimension,
            Mode = mode,
            Limit = limit
        };

    [Fact]
    public void BuildQuery_DefaultShape_UsesDayBucket_ExactUserCount()
    {
        var (sql, _) = GetActiveUsersQuery.BuildQuery(Request());

        sql.Should().Contain("toStartOfDay(timestamp) AS bucket");
        sql.Should().Contain("uniqExact(user_id) AS active_count");
        sql.Should().Contain("FROM nealytics_core.global_events");
        sql.Should().Contain("project_id = {projectId:String} AND tenant_id = {tenantId:String}");
        sql.Should().Contain("timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64}");
        sql.Should().Contain("GROUP BY bucket ORDER BY bucket ASC LIMIT {limit:Int32}");
    }

    [Fact]
    public void BuildQuery_MonthSessionApprox_UsesWhitelistedIdentifiers()
    {
        var (sql, _) = GetActiveUsersQuery.BuildQuery(
            Request(ActiveUsersInterval.Month, ActiveDimension.Session, ActiveCountMode.Approx));

        sql.Should().Contain("toStartOfMonth(timestamp) AS bucket");
        sql.Should().Contain("uniq(session_id) AS active_count");
        sql.Should().NotContain("uniqExact");
    }

    [Fact]
    public void BuildQuery_Parameters_HaveExpectedKeysOrderAndValues()
    {
        ActiveUsersRequest request = Request(limit: 42);
        var (_, parameters) = GetActiveUsersQuery.BuildQuery(request);

        parameters.Select(p => p.Key).Should().Equal("projectId", "tenantId", "fromTimestamp", "toTimestamp", "limit");
        parameters[0].Value.Should().Be("proj");
        parameters[1].Value.Should().Be("tenant");
        parameters[2].Value.Should().Be(request.From);
        parameters[3].Value.Should().Be(request.To);
        parameters[4].Value.Should().Be(42);
    }

    [Fact]
    public void BuildQuery_NeverInterpolatesTenantOrProjectValues_IntoSql()
    {
        ActiveUsersRequest request = new ActiveUsersRequest
        {
            ProjectId = "acme-injected-9f",
            TenantId = "north-injected-7c",
            From = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc),
            Interval = ActiveUsersInterval.Day,
            Dimension = ActiveDimension.User,
            Mode = ActiveCountMode.Exact,
            Limit = 100
        };

        var (sql, parameters) = GetActiveUsersQuery.BuildQuery(request);

        sql.Should().NotContain("acme-injected-9f");
        sql.Should().NotContain("north-injected-7c");
        parameters.Should().Contain(p => p.Key == "projectId" && (string)p.Value! == "acme-injected-9f");
        parameters.Should().Contain(p => p.Key == "tenantId" && (string)p.Value! == "north-injected-7c");
    }
}

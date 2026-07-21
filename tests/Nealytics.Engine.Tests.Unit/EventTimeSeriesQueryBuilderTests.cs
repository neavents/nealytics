using FluentAssertions;
using Nealytics.Engine.Features.GetEventTimeSeries;

namespace Nealytics.Engine.Tests.Unit;

public class EventTimeSeriesQueryBuilderTests
{
    private static EventTimeSeriesRequest BaseRequest(
        TimeSeriesInterval interval = TimeSeriesInterval.Hour, string? eventType = null) =>
        new EventTimeSeriesRequest
        {
            ProjectId = "proj",
            TenantId = "tenant",
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Interval = interval,
            EventType = eventType,
            Limit = 500
        };

    [Theory]
    [InlineData(TimeSeriesInterval.Minute, "toStartOfMinute")]
    [InlineData(TimeSeriesInterval.Hour, "toStartOfHour")]
    [InlineData(TimeSeriesInterval.Day, "toStartOfDay")]
    public void BuildQuery_MapsIntervalToBucketFunction(TimeSeriesInterval interval, string expectedFunction)
    {
        (string sql, _) = GetEventTimeSeriesQuery.BuildQuery(BaseRequest(interval));

        sql.Should().Contain($"{expectedFunction}(timestamp) AS bucket");
        sql.Should().Contain("count() AS event_count");
        sql.Should().Contain("GROUP BY bucket ORDER BY bucket ASC LIMIT {limit:Int32}");
    }

    [Fact]
    public void BuildQuery_AlwaysFiltersByTenantProjectAndTimeRange()
    {
        (string sql, var parameters) = GetEventTimeSeriesQuery.BuildQuery(BaseRequest());

        sql.Should().Contain("WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}");
        sql.Should().Contain("AND timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64}");

        parameters.Should().ContainSingle(p => p.Key == "projectId");
        parameters.Should().ContainSingle(p => p.Key == "tenantId");
        parameters.Should().ContainSingle(p => p.Key == "fromTimestamp");
        parameters.Should().ContainSingle(p => p.Key == "toTimestamp");
        parameters.Should().ContainSingle(p => p.Key == "limit");
        parameters.Should().NotContain(p => p.Key == "eventType");
    }

    [Fact]
    public void BuildQuery_WithEventType_AppendsPredicateAndParameter()
    {
        (string sql, var parameters) = GetEventTimeSeriesQuery.BuildQuery(
            BaseRequest(eventType: "purchase"));

        sql.Should().Contain("AND event_type = {eventType:String}");
        parameters.Should().ContainSingle(p => p.Key == "eventType" && (string)p.Value! == "purchase");
    }

    [Fact]
    public void BuildQuery_WithEmptyEventType_OmitsPredicate()
    {
        (string sql, var parameters) = GetEventTimeSeriesQuery.BuildQuery(
            BaseRequest(eventType: ""));

        sql.Should().NotContain("event_type =");
        parameters.Should().NotContain(p => p.Key == "eventType");
    }

    private static EventTimeSeriesRequest GroupedRequest(TimeSeriesGroupBy groupBy, string? eventType = null) =>
        new EventTimeSeriesRequest
        {
            ProjectId = "proj",
            TenantId = "tenant",
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Interval = TimeSeriesInterval.Hour,
            EventType = eventType,
            GroupBy = groupBy,
            Limit = 500
        };

    [Fact]
    public void BuildQuery_WithoutGroupBy_HasNoSeriesColumn()
    {
        (string sql, _) = GetEventTimeSeriesQuery.BuildQuery(BaseRequest());
        sql.Should().NotContain("series");
        sql.Should().Contain("GROUP BY bucket ORDER BY bucket ASC");
    }

    [Theory]
    [InlineData(TimeSeriesGroupBy.EventType, "event_type")]
    [InlineData(TimeSeriesGroupBy.ItemId, "item_id")]
    [InlineData(TimeSeriesGroupBy.SessionId, "session_id")]
    public void BuildQuery_WithGroupBy_SelectsWhitelistedSeriesColumn(TimeSeriesGroupBy groupBy, string column)
    {
        (string sql, _) = GetEventTimeSeriesQuery.BuildQuery(GroupedRequest(groupBy));

        sql.Should().Contain($"{column} AS series");
        sql.Should().Contain("GROUP BY bucket, series ORDER BY bucket ASC, series ASC LIMIT {limit:Int32}");
    }

    [Fact]
    public void BuildQuery_GroupedWithEventTypeFilter_KeepsBothPredicateAndSeries()
    {
        (string sql, var parameters) = GetEventTimeSeriesQuery.BuildQuery(
            GroupedRequest(TimeSeriesGroupBy.ItemId, eventType: "purchase"));

        sql.Should().Contain("item_id AS series");
        sql.Should().Contain("AND event_type = {eventType:String}");
        parameters.Should().ContainSingle(p => p.Key == "eventType" && (string)p.Value! == "purchase");
    }

    [Fact]
    public void BuildQuery_Grouped_NeverInterpolatesGroupColumnFromUserInput()
    {
        (string sql, _) = GetEventTimeSeriesQuery.BuildQuery(GroupedRequest(TimeSeriesGroupBy.SessionId));
        sql.Should().Contain("session_id AS series");
        sql.Should().NotContain("{groupBy");
    }
}

public class TimeSeriesGroupByParserTests
{
    [Theory]
    [InlineData("event_type", TimeSeriesGroupBy.EventType)]
    [InlineData("item_id", TimeSeriesGroupBy.ItemId)]
    [InlineData("session_id", TimeSeriesGroupBy.SessionId)]
    public void TryParse_ValidValues_ReturnsTrue(string raw, TimeSeriesGroupBy expected)
    {
        TimeSeriesGroupByParser.TryParse(raw, out var groupBy).Should().BeTrue();
        groupBy.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("user_id")]
    [InlineData("EventType")]
    public void TryParse_InvalidValues_ReturnsFalseAndNone(string? raw)
    {
        TimeSeriesGroupByParser.TryParse(raw, out var groupBy).Should().BeFalse();
        groupBy.Should().Be(TimeSeriesGroupBy.None);
    }

    [Theory]
    [InlineData(TimeSeriesGroupBy.EventType, "event_type")]
    [InlineData(TimeSeriesGroupBy.ItemId, "item_id")]
    [InlineData(TimeSeriesGroupBy.SessionId, "session_id")]
    public void ToColumn_MapsWhitelistedColumn(TimeSeriesGroupBy groupBy, string expected)
    {
        TimeSeriesGroupByParser.ToColumn(groupBy).Should().Be(expected);
    }

    [Fact]
    public void ToColumn_None_Throws()
    {
        Action act = () => TimeSeriesGroupByParser.ToColumn(TimeSeriesGroupBy.None);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public class TimeSeriesIntervalParserTests
{
    [Theory]
    [InlineData("minute", TimeSeriesInterval.Minute)]
    [InlineData("hour", TimeSeriesInterval.Hour)]
    [InlineData("day", TimeSeriesInterval.Day)]
    public void TryParse_ValidValues_ReturnsTrueAndInterval(string value, TimeSeriesInterval expected)
    {
        bool ok = TimeSeriesIntervalParser.TryParse(value, out TimeSeriesInterval interval);

        ok.Should().BeTrue();
        interval.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("week")]
    [InlineData("HOUR")]
    public void TryParse_InvalidValues_ReturnsFalseAndDefaultsToHour(string? value)
    {
        bool ok = TimeSeriesIntervalParser.TryParse(value, out TimeSeriesInterval interval);

        ok.Should().BeFalse();
        interval.Should().Be(TimeSeriesInterval.Hour);
    }

    [Theory]
    [InlineData(TimeSeriesInterval.Minute, "minute")]
    [InlineData(TimeSeriesInterval.Hour, "hour")]
    [InlineData(TimeSeriesInterval.Day, "day")]
    public void ToWireFormat_ReturnsCanonicalString(TimeSeriesInterval interval, string expected)
    {
        TimeSeriesIntervalParser.ToWireFormat(interval).Should().Be(expected);
    }

    [Theory]
    [InlineData(TimeSeriesInterval.Minute, "toStartOfMinute")]
    [InlineData(TimeSeriesInterval.Hour, "toStartOfHour")]
    [InlineData(TimeSeriesInterval.Day, "toStartOfDay")]
    public void ToBucketFunction_MapsInterval(TimeSeriesInterval interval, string expected)
    {
        TimeSeriesIntervalParser.ToBucketFunction(interval).Should().Be(expected);
    }

    [Fact]
    public void ToWireFormat_UndefinedEnum_Throws()
    {
        Action act = () => TimeSeriesIntervalParser.ToWireFormat((TimeSeriesInterval)99);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToBucketFunction_UndefinedEnum_Throws()
    {
        Action act = () => TimeSeriesIntervalParser.ToBucketFunction((TimeSeriesInterval)99);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

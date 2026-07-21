using FluentAssertions;
using Nealytics.Engine.Features.GetTopEvents;

namespace Nealytics.Engine.Tests.Unit;

public class TopDimensionParserTests
{
    [Theory]
    [InlineData("event_type", TopDimension.EventType)]
    [InlineData("item_id", TopDimension.ItemId)]
    public void TryParse_ValidValues_ReturnsTrue(string raw, TopDimension expected)
    {
        TopDimensionParser.TryParse(raw, out var dimension).Should().BeTrue();
        dimension.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("session_id")]
    [InlineData("EVENT_TYPE")]
    public void TryParse_InvalidValues_ReturnsFalseAndDefaultsToEventType(string? raw)
    {
        TopDimensionParser.TryParse(raw, out var dimension).Should().BeFalse();
        dimension.Should().Be(TopDimension.EventType);
    }

    [Theory]
    [InlineData(TopDimension.EventType, "event_type")]
    [InlineData(TopDimension.ItemId, "item_id")]
    public void ToColumn_MapsWhitelistedColumn(TopDimension dimension, string expected)
    {
        TopDimensionParser.ToColumn(dimension).Should().Be(expected);
    }

    [Fact]
    public void ExcludesNull_OnlyForItemId()
    {
        TopDimensionParser.ExcludesNull(TopDimension.ItemId).Should().BeTrue();
        TopDimensionParser.ExcludesNull(TopDimension.EventType).Should().BeFalse();
    }

    [Fact]
    public void Mappers_UndefinedEnum_Throw()
    {
        Action column = () => TopDimensionParser.ToColumn((TopDimension)99);
        Action wire = () => TopDimensionParser.ToWireFormat((TopDimension)99);
        column.Should().Throw<ArgumentOutOfRangeException>();
        wire.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public class TopEventsRequestFactoryTests
{
    private static readonly DateTime Now = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    private static TopEventsRequestResult Create(
        string? projectId = "proj", string? tenantId = "tenant", string? limit = null,
        string? dimension = null, string? from = null, string? to = null,
        int maxLimit = 1000, int defaultRangeHours = 24)
        => TopEventsRequestFactory.Create(projectId, tenantId, limit, dimension, from, to, maxLimit, defaultRangeHours, Now);

    [Theory]
    [InlineData(null, "t")]
    [InlineData("p", "")]
    public void MissingClaims_Returns403(string? projectId, string? tenantId)
    {
        var result = Create(projectId, tenantId);
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(TopEventsRequestFactory.StatusForbidden);
    }

    [Fact]
    public void OverLengthClaim_Returns400()
    {
        Create(tenantId: new string('t', 257)).ErrorStatusCode.Should().Be(TopEventsRequestFactory.StatusBadRequest);
    }

    [Fact]
    public void Defaults_AreEventTypeDimension_And20Limit_WithTrailingWindow()
    {
        var result = Create();
        result.Success.Should().BeTrue();
        result.Request.Dimension.Should().Be(TopDimension.EventType);
        result.Request.Limit.Should().Be(20);
        result.Request.To.Should().Be(Now);
        result.Request.From.Should().Be(Now.AddHours(-24));
    }

    [Fact]
    public void DefaultLimit_IsClampedToMax_WhenMaxBelow20()
    {
        Create(maxLimit: 5).Request.Limit.Should().Be(5);
    }

    [Fact]
    public void ValidDimension_IsParsed()
    {
        Create(dimension: "item_id").Request.Dimension.Should().Be(TopDimension.ItemId);
    }

    [Fact]
    public void InvalidDimension_Returns400()
    {
        var result = Create(dimension: "user_id");
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(TopEventsRequestFactory.StatusBadRequest);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("500000", 1000)]
    [InlineData("50", 50)]
    public void Limit_Clamping(string raw, int expected)
    {
        Create(limit: raw).Request.Limit.Should().Be(expected);
    }

    [Fact]
    public void Limit_InvalidText_FallsBackToDefault()
    {
        Create(limit: "not-a-number").Request.Limit.Should().Be(20);
    }

    [Fact]
    public void FromAfterTo_Returns400()
    {
        var result = Create(from: "2026-07-20T00:00:00Z", to: "2026-07-01T00:00:00Z");
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(TopEventsRequestFactory.StatusBadRequest);
    }

    [Fact]
    public void ExplicitRange_IsParsedAsUtc()
    {
        var result = Create(from: "2026-06-01T00:00:00Z", to: "2026-06-08T00:00:00Z");
        result.Request.From.Should().Be(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        result.Request.To.Should().Be(new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc));
    }
}

public class TopEventsQueryBuilderTests
{
    private static TopEventsRequest Request(TopDimension dimension = TopDimension.EventType, int limit = 20) =>
        new TopEventsRequest
        {
            ProjectId = "proj",
            TenantId = "tenant",
            From = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc),
            Dimension = dimension,
            Limit = limit
        };

    [Fact]
    public void BuildQuery_EventType_HasNoNullExclusion()
    {
        var (sql, _) = GetTopEventsQuery.BuildQuery(Request(TopDimension.EventType));

        sql.Should().Contain("SELECT event_type AS key, count() AS event_count");
        sql.Should().Contain("GROUP BY key ORDER BY event_count DESC LIMIT {limit:Int32}");
        sql.Should().NotContain("IS NOT NULL");
    }

    [Fact]
    public void BuildQuery_ItemId_ExcludesNullKeys()
    {
        var (sql, _) = GetTopEventsQuery.BuildQuery(Request(TopDimension.ItemId));

        sql.Should().Contain("SELECT item_id AS key");
        sql.Should().Contain("AND item_id IS NOT NULL");
    }

    [Fact]
    public void BuildQuery_AlwaysFiltersByTenantProjectAndRange()
    {
        var (sql, parameters) = GetTopEventsQuery.BuildQuery(Request());

        sql.Should().Contain("WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}");
        sql.Should().Contain("timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64}");
        parameters.Select(p => p.Key).Should().Equal("projectId", "tenantId", "fromTimestamp", "toTimestamp", "limit");
    }

    [Fact]
    public void BuildQuery_ParameterValues_AreCorrect()
    {
        TopEventsRequest request = Request(limit: 7);
        var (_, parameters) = GetTopEventsQuery.BuildQuery(request);

        parameters[0].Value.Should().Be("proj");
        parameters[1].Value.Should().Be("tenant");
        parameters[2].Value.Should().Be(request.From);
        parameters[3].Value.Should().Be(request.To);
        parameters[4].Value.Should().Be(7);
    }
}

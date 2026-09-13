using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Features.GetTopEvents;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;


public class TopEventsRequestFactoryTests
{
    private static readonly DateTime Now = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    private static TopEventsRequestResult Create(
        string? projectId = "proj", string? tenantId = "tenant", string? limit = null,
        string? dimension = null, string? from = null, string? to = null,
        string? traffic = null, string? exact = null, int maxLimit = 1000, int defaultRangeHours = 24)
        => TopEventsRequestFactory.Create(
            projectId, tenantId, limit, dimension, TestColumns(), TestMeasures(),
            from, to, traffic, exact, maxLimit, defaultRangeHours, Now);

    private static QueryColumns TestColumns() =>
        new(new DimensionRegistry(new TelemetryEngineOptions
        {
            Dimensions = [new DimensionOptions { Name = "widget_id", Type = "String" }],
        }));

    private static MeasureRegistry TestMeasures()
    {
        TelemetryEngineOptions options = new()
        {
            Measures = [new MeasureOptions { Name = "dwell_ms", Type = "UInt32" }],
        };

        return new MeasureRegistry(options, new DimensionRegistry(options));
    }

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
        result.Request.DimensionColumn.Should().Be("event_type");
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
        Create(dimension: "object_id").Request.DimensionColumn.Should().Be("object_id");
    }

    [Theory]
    [InlineData("event_id")]
    [InlineData("metadata_json")]
    [InlineData("timestamp")]
    [InlineData("EVENT_TYPE")]
    [InlineData("nope")]
    [InlineData("object_id; DROP TABLE global_events")]
    public void InvalidDimension_Returns400(string dimension)
    {
        var result = Create(dimension: dimension);
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(TopEventsRequestFactory.StatusBadRequest);
    }

    [Fact]
    public void DeclaredDimension_IsRankable()
    {
        Create(dimension: "widget_id").Request.DimensionColumn.Should().Be(
            "widget_id",
            "a declared dimension was invisible to this endpoint before");
    }

    [Fact]
    public void Measure_IsNotRankable()
    {
        var result = Create(dimension: "dwell_ms");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("is a declared measure, not a dimension");
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

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheLiteralTrueOptsIntoTheSlowPath(string? raw, bool expected)
    {
        // Strict on purpose, and matching /breakdown exactly. A near-miss silently making every
        // query in a dashboard slower is worse than a near-miss doing nothing, and two endpoints
        // disagreeing about how "exact" is spelled would be worse than either.
        Create(exact: raw).Request.Exact.Should().Be(expected);
    }
}

public class TopEventsQueryBuilderTests
{
    private static TopEventsRequest Request(
        string dimensionColumn = "event_type", int limit = 20, bool exact = false) =>
        new TopEventsRequest
        {
            ProjectId = "proj",
            TenantId = "tenant",
            From = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc),
            DimensionColumn = dimensionColumn,
            Limit = limit,
            Exact = exact,
        };

    /// <summary>
    /// Counting rows versus counting distinct events.
    ///
    /// No query uses FINAL, so a WAL replay leaves duplicates counted until a background merge
    /// collapses them. The default stays the fast one and says so; exactness is opt-in and slower,
    /// which is the honest trade rather than a hidden one.
    /// </summary>
    [Fact]
    public void BuildQuery_ByDefault_CountsRows()
    {
        var (sql, _) = GetTopEventsQuery.BuildQuery(Request());

        sql.Should().Contain("count() AS event_count");
        sql.Should().NotContain("uniqExact(event_id)");
    }

    [Fact]
    public void BuildQuery_WhenExact_CountsDistinctEventIds()
    {
        var (sql, _) = GetTopEventsQuery.BuildQuery(Request(exact: true));

        sql.Should().Contain("uniqExact(event_id) AS event_count");
    }


    [Fact]
    public void BuildQuery_EventType_HasNoNullExclusion()
    {
        var (sql, _) = GetTopEventsQuery.BuildQuery(Request("event_type"));

        sql.Should().Contain("SELECT event_type AS key, count() AS event_count");
        sql.Should().Contain("GROUP BY key ORDER BY event_count DESC LIMIT {limit:Int32}");
        sql.Should().NotContain("IS NOT NULL");
    }

    [Fact]
    public void BuildQuery_ItemId_ExcludesNullKeys()
    {
        var (sql, _) = GetTopEventsQuery.BuildQuery(Request("object_id"));

        sql.Should().Contain("SELECT object_id AS key");
        sql.Should().Contain("AND object_id IS NOT NULL");
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

public class TopDimensionRulesTests
{
    [Fact]
    public void EventType_IsNeverNull_SoNullsAreNotExcluded()
    {
        TopDimensionRules.ExcludesNull("event_type").Should().BeFalse();
    }

    [Theory]
    [InlineData("object_id")]
    [InlineData("widget_id")]
    [InlineData("user_id")]
    public void NullableColumns_ExcludeNulls_SoTheLeaderboardRanksValuesNotAbsence(string column)
    {
        TopDimensionRules.ExcludesNull(column).Should().BeTrue();
    }
}

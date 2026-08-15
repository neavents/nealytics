using FluentAssertions;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Features.GetEventTimeSeries;

namespace Nealytics.Engine.Tests.Unit;

public class EventTimeSeriesRequestFactoryTests
{
    private const int MaxLimit = 5000;
    private const int DefaultRangeHours = 24;
    private static readonly DateTime Now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static EventTimeSeriesRequestResult Create(
        string? projectId = "proj", string? tenantId = "tenant", string? limit = null,
        string? interval = null, string? from = null, string? to = null, string? eventType = null,
        string? groupBy = null,
        string? tz = null,
        string? traffic = null,
        string[]? filters = null)
        => EventTimeSeriesRequestFactory.Create(
            projectId, tenantId, limit, interval, from, to, eventType, groupBy, tz, traffic,
            filters ?? [],
            TestColumns(), TestMeasures(), MaxLimit, DefaultRangeHours, Now);

    private static Nealytics.Engine.Features.GetBreakdown.BreakdownColumns TestColumns() =>
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
    [InlineData(null, "tenant")]
    [InlineData("proj", "")]
    public void Create_MissingClaims_Returns403(string? projectId, string? tenantId)
    {
        EventTimeSeriesRequestResult result = Create(projectId, tenantId);
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(EventTimeSeriesRequestFactory.StatusForbidden);
    }

    [Fact]
    public void Create_ClaimTooLong_Returns400()
    {
        EventTimeSeriesRequestResult result = Create(projectId: new string('p', 257));
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(EventTimeSeriesRequestFactory.StatusBadRequest);
    }

    [Theory]
    [InlineData("minute", TimeSeriesInterval.Minute)]
    [InlineData("hour", TimeSeriesInterval.Hour)]
    [InlineData("day", TimeSeriesInterval.Day)]
    public void Create_ValidInterval_IsParsed(string interval, TimeSeriesInterval expected)
    {
        EventTimeSeriesRequestResult result = Create(interval: interval);
        result.Success.Should().BeTrue();
        result.Request.Interval.Should().Be(expected);
    }

    [Fact]
    public void Create_NoInterval_DefaultsToHour()
    {
        EventTimeSeriesRequestResult result = Create();
        result.Success.Should().BeTrue();
        result.Request.Interval.Should().Be(TimeSeriesInterval.Hour);
    }

    [Theory]
    [InlineData("week")]
    [InlineData("HOUR")]
    [InlineData("year")]
    public void Create_InvalidInterval_Returns400(string interval)
    {
        EventTimeSeriesRequestResult result = Create(interval: interval);
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(EventTimeSeriesRequestFactory.StatusBadRequest);
        result.ErrorMessage.Should().Contain("interval");
    }

    [Fact]
    public void Create_NoLimit_DefaultsToMaxLimit()
    {
        EventTimeSeriesRequestResult result = Create();
        result.Request.Limit.Should().Be(MaxLimit);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("99999", MaxLimit)]
    [InlineData("100", 100)]
    public void Create_LimitClamping(string limitRaw, int expected)
    {
        EventTimeSeriesRequestResult result = Create(limit: limitRaw);
        result.Request.Limit.Should().Be(expected);
    }

    [Fact]
    public void Create_FromAfterTo_Returns400()
    {
        EventTimeSeriesRequestResult result = Create(
            from: "2030-01-01T00:00:00Z", to: "2020-01-01T00:00:00Z");
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(EventTimeSeriesRequestFactory.StatusBadRequest);
    }

    [Fact]
    public void Create_NoDates_UsesTrailingWindow()
    {
        EventTimeSeriesRequestResult result = Create();
        result.Request.To.Should().Be(Now);
        result.Request.From.Should().Be(Now.AddHours(-DefaultRangeHours));
    }

    [Fact]
    public void Create_EventTypeTooLong_Returns400()
    {
        EventTimeSeriesRequestResult result = Create(eventType: new string('e', 257));
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(EventTimeSeriesRequestFactory.StatusBadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankEventType_NormalizesToNull(string blank)
    {
        EventTimeSeriesRequestResult result = Create(eventType: blank);
        result.Success.Should().BeTrue();
        result.Request.EventType.Should().BeNull();
    }

    [Fact]
    public void Create_EventType_IsPreserved()
    {
        EventTimeSeriesRequestResult result = Create(eventType: "signup");
        result.Request.EventType.Should().Be("signup");
    }

    [Fact]
    public void Create_NoGroupBy_LeavesTheSeriesColumnUnset()
    {
        EventTimeSeriesRequestResult result = Create();
        result.Success.Should().BeTrue();
        result.Request.GroupByColumn.Should().BeNull();
    }

    [Theory]
    [InlineData("event_type")]
    [InlineData("object_id")]
    [InlineData("session_id")]
    public void Create_ValidGroupBy_IsParsed(string raw)
    {
        EventTimeSeriesRequestResult result = Create(groupBy: raw);
        result.Success.Should().BeTrue();
        result.Request.GroupByColumn.Should().Be(raw);
    }

    [Fact]
    public void Create_GroupByADeclaredDimension_IsAccepted()
    {
        EventTimeSeriesRequestResult result = Create(groupBy: "widget_id");

        result.Success.Should().BeTrue("a declared dimension was invisible to this endpoint before");
        result.Request.GroupByColumn.Should().Be("widget_id");
    }

    [Fact]
    public void Create_GroupByAMeasure_Returns400ExplainingTheKindMistake()
    {
        EventTimeSeriesRequestResult result = Create(groupBy: "dwell_ms");

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(400);
        result.ErrorMessage.Should().Contain("is a declared measure, not a dimension");
    }

    [Theory]
    [InlineData("event_id")]
    [InlineData("metadata_json")]
    [InlineData("EVENT_TYPE")]
    [InlineData("timestamp")]
    [InlineData("nope")]
    [InlineData("session_id; DROP TABLE global_events")]
    public void Create_InvalidGroupBy_Returns400(string raw)
    {
        EventTimeSeriesRequestResult result = Create(groupBy: raw);
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(EventTimeSeriesRequestFactory.StatusBadRequest);
        result.ErrorMessage.Should().Contain("groupBy");
    }
}

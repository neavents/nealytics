using FluentAssertions;
using Nealytics.Engine.Features.GetEventTimeSeries;

namespace Nealytics.Engine.Tests.Unit;

public class EventTimeSeriesRequestFactoryTests
{
    private const int MaxLimit = 5000;
    private const int DefaultRangeHours = 24;
    private static readonly DateTime Now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static EventTimeSeriesRequestResult Create(
        string? projectId = "proj", string? tenantId = "tenant", string? limit = null,
        string? interval = null, string? from = null, string? to = null, string? eventType = null)
        => EventTimeSeriesRequestFactory.Create(
            projectId, tenantId, limit, interval, from, to, eventType, MaxLimit, DefaultRangeHours, Now);

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
}

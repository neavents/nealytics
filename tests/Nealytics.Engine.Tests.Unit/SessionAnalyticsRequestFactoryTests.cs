using FluentAssertions;
using Nealytics.Engine.Features.GetSessionAnalytics;

namespace Nealytics.Engine.Tests.Unit;

public class SessionAnalyticsRequestFactoryTests
{
    private const int MaxLimit = 1000;
    private const int DefaultRangeHours = 24;
    private static readonly DateTime Now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static SessionAnalyticsRequestResult Create(
        string? projectId = "proj", string? tenantId = "tenant",
        string? limit = null, string? from = null, string? to = null)
        => SessionAnalyticsRequestFactory.Create(
            projectId, tenantId, limit, from, to, MaxLimit, DefaultRangeHours, Now);

    [Theory]
    [InlineData(null, "tenant")]
    [InlineData("proj", null)]
    [InlineData(" ", "tenant")]
    public void Create_MissingClaims_Returns403(string? projectId, string? tenantId)
    {
        SessionAnalyticsRequestResult result = Create(projectId, tenantId);

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(SessionAnalyticsRequestFactory.StatusForbidden);
    }

    [Fact]
    public void Create_ClaimTooLong_Returns400()
    {
        SessionAnalyticsRequestResult result = Create(tenantId: new string('t', 300));

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(SessionAnalyticsRequestFactory.StatusBadRequest);
    }

    [Fact]
    public void Create_NoDates_DefaultsToTrailingWindow()
    {
        SessionAnalyticsRequestResult result = Create();

        result.Success.Should().BeTrue();
        result.Request.To.Should().Be(Now);
        result.Request.From.Should().Be(Now.AddHours(-DefaultRangeHours));
    }

    [Fact]
    public void Create_ExplicitDates_AreUsed()
    {
        SessionAnalyticsRequestResult result = Create(
            from: "2026-01-01T00:00:00Z", to: "2026-02-01T00:00:00Z");

        result.Success.Should().BeTrue();
        result.Request.From.ToUniversalTime().Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        result.Request.To.ToUniversalTime().Should().Be(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Create_FromAfterTo_Returns400()
    {
        SessionAnalyticsRequestResult result = Create(
            from: "2030-01-01T00:00:00Z", to: "2020-01-01T00:00:00Z");

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(SessionAnalyticsRequestFactory.StatusBadRequest);
        result.ErrorMessage.Should().Contain("before or equal");
    }

    [Fact]
    public void Create_EqualFromTo_IsAccepted()
    {
        SessionAnalyticsRequestResult result = Create(
            from: "2026-01-01T00:00:00Z", to: "2026-01-01T00:00:00Z");

        result.Success.Should().BeTrue();
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("2000", MaxLimit)]
    [InlineData("250", 250)]
    [InlineData("bad", SessionAnalyticsRequestFactory.DefaultLimit)]
    public void Create_LimitClamping(string limitRaw, int expected)
    {
        SessionAnalyticsRequestResult result = Create(limit: limitRaw);

        result.Request.Limit.Should().Be(expected);
    }

    [Fact]
    public void Create_InvalidDate_FallsBackToDefault()
    {
        SessionAnalyticsRequestResult result = Create(from: "not-a-date");

        result.Success.Should().BeTrue();
        result.Request.From.Should().Be(Now.AddHours(-DefaultRangeHours));
    }
}

using FluentAssertions;
using Nealytics.Engine.Features.GetActiveUsers;

namespace Nealytics.Engine.Tests.Unit;

public class ActiveUsersRequestFactoryTests
{
    private static readonly DateTime Now = new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    private static ActiveUsersRequestResult Create(
        string? projectId = "proj", string? tenantId = "tenant", string? limit = null,
        string? interval = null, string? by = null, string? mode = null,
        string? from = null, string? to = null, int maxLimit = 1000, int defaultRangeHours = 24)
        => ActiveUsersRequestFactory.Create(projectId, tenantId, limit, interval, by, mode, from, to, maxLimit, defaultRangeHours, Now);

    [Theory]
    [InlineData(null, "t")]
    [InlineData("p", null)]
    [InlineData("", "t")]
    [InlineData("  ", "t")]
    public void MissingClaims_Returns403(string? projectId, string? tenantId)
    {
        var result = Create(projectId, tenantId);
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(ActiveUsersRequestFactory.StatusForbidden);
    }

    [Fact]
    public void OverLengthClaim_Returns400()
    {
        var result = Create(projectId: new string('a', 257));
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(ActiveUsersRequestFactory.StatusBadRequest);
    }

    [Fact]
    public void Defaults_AreDayUserExact_WithTrailingWindow()
    {
        var result = Create();
        result.Success.Should().BeTrue();
        result.Request.Interval.Should().Be(ActiveUsersInterval.Day);
        result.Request.Dimension.Should().Be(ActiveDimension.User);
        result.Request.Mode.Should().Be(ActiveCountMode.Exact);
        result.Request.Limit.Should().Be(1000);
        result.Request.To.Should().Be(Now);
        result.Request.From.Should().Be(Now.AddHours(-24));
    }

    [Theory]
    [InlineData("month", ActiveUsersInterval.Month)]
    [InlineData("day", ActiveUsersInterval.Day)]
    public void ValidInterval_IsParsed(string raw, ActiveUsersInterval expected)
    {
        Create(interval: raw).Request.Interval.Should().Be(expected);
    }

    [Fact]
    public void InvalidInterval_Returns400()
    {
        var result = Create(interval: "week");
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(ActiveUsersRequestFactory.StatusBadRequest);
    }

    [Fact]
    public void InvalidBy_Returns400()
    {
        var result = Create(by: "device");
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(ActiveUsersRequestFactory.StatusBadRequest);
    }

    [Fact]
    public void InvalidMode_Returns400()
    {
        var result = Create(mode: "estimate");
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(ActiveUsersRequestFactory.StatusBadRequest);
    }

    [Theory]
    [InlineData("session", ActiveDimension.Session)]
    [InlineData("user", ActiveDimension.User)]
    public void ValidBy_IsParsed(string raw, ActiveDimension expected)
    {
        Create(by: raw).Request.Dimension.Should().Be(expected);
    }

    [Theory]
    [InlineData("approx", ActiveCountMode.Approx)]
    [InlineData("exact", ActiveCountMode.Exact)]
    public void ValidMode_IsParsed(string raw, ActiveCountMode expected)
    {
        Create(mode: raw).Request.Mode.Should().Be(expected);
    }

    [Fact]
    public void Limit_ClampsToMax()
    {
        Create(limit: "999999", maxLimit: 500).Request.Limit.Should().Be(500);
    }

    [Fact]
    public void Limit_ClampsToOne_WhenZeroOrNegative()
    {
        Create(limit: "0").Request.Limit.Should().Be(1);
        Create(limit: "-5").Request.Limit.Should().Be(1);
    }

    [Fact]
    public void Limit_InvalidText_FallsBackToMax()
    {
        Create(limit: "abc", maxLimit: 321).Request.Limit.Should().Be(321);
    }

    [Fact]
    public void FromAfterTo_Returns400()
    {
        var result = Create(from: "2026-07-20T00:00:00Z", to: "2026-07-01T00:00:00Z");
        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(ActiveUsersRequestFactory.StatusBadRequest);
    }

    [Fact]
    public void FromEqualsTo_IsAllowed()
    {
        var result = Create(from: "2026-07-10T00:00:00Z", to: "2026-07-10T00:00:00Z");
        result.Success.Should().BeTrue();
        result.Request.From.Should().Be(result.Request.To);
    }

    [Fact]
    public void ExplicitRange_IsParsedAsUtc()
    {
        var result = Create(from: "2026-06-01T00:00:00Z", to: "2026-06-08T00:00:00Z");
        result.Success.Should().BeTrue();
        result.Request.From.Should().Be(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        result.Request.To.Should().Be(new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc));
    }
}

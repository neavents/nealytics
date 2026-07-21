using FluentAssertions;
using Nealytics.Engine.Features.GetProjectTimeline;

namespace Nealytics.Engine.Tests.Unit;

public class TimelineRequestFactoryTests
{
    private const int MaxLimit = 1000;

    private static TimelineRequestResult Create(
        string? projectId = "proj", string? tenantId = "tenant", string? limit = null,
        string? before = null, string? eventType = null, string? sessionId = null, string? itemId = null,
        string? metaKey = null, string? metaValue = null)
        => TimelineRequestFactory.Create(projectId, tenantId, limit, before, eventType, sessionId, itemId, metaKey, metaValue, MaxLimit);

    [Theory]
    [InlineData(null, "tenant")]
    [InlineData("proj", null)]
    [InlineData("", "tenant")]
    [InlineData("proj", "   ")]
    public void Create_MissingClaims_Returns403(string? projectId, string? tenantId)
    {
        TimelineRequestResult result = Create(projectId, tenantId);

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(TimelineRequestFactory.StatusForbidden);
    }

    [Fact]
    public void Create_ClaimTooLong_Returns400()
    {
        TimelineRequestResult result = Create(projectId: new string('x', 257));

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(TimelineRequestFactory.StatusBadRequest);
    }

    [Fact]
    public void Create_NoLimit_DefaultsTo100()
    {
        TimelineRequestResult result = Create();

        result.Success.Should().BeTrue();
        result.Request.Limit.Should().Be(TimelineRequestFactory.DefaultLimit);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("50", 50)]
    [InlineData("999999", MaxLimit)]
    [InlineData("not-a-number", TimelineRequestFactory.DefaultLimit)]
    [InlineData("", TimelineRequestFactory.DefaultLimit)]
    public void Create_LimitParsingAndClamping(string limitRaw, int expected)
    {
        TimelineRequestResult result = Create(limit: limitRaw);

        result.Success.Should().BeTrue();
        result.Request.Limit.Should().Be(expected);
    }

    [Fact]
    public void Create_ValidBeforeCursor_IsParsedAsUtc()
    {
        TimelineRequestResult result = Create(before: "2026-05-01T08:00:00Z");

        result.Success.Should().BeTrue();
        result.Request.Before.Should().NotBeNull();
        result.Request.Before!.Value.ToUniversalTime()
            .Should().Be(new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Create_InvalidBeforeCursor_IsIgnored()
    {
        TimelineRequestResult result = Create(before: "garbage");

        result.Success.Should().BeTrue();
        result.Request.Before.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankFilters_NormalizeToNull(string blank)
    {
        TimelineRequestResult result = Create(eventType: blank, sessionId: blank, itemId: blank);

        result.Success.Should().BeTrue();
        result.Request.EventType.Should().BeNull();
        result.Request.SessionId.Should().BeNull();
        result.Request.ItemId.Should().BeNull();
    }

    [Fact]
    public void Create_PopulatedFilters_ArePreserved()
    {
        TimelineRequestResult result = Create(eventType: "click", sessionId: "s1", itemId: "/x");

        result.Request.EventType.Should().Be("click");
        result.Request.SessionId.Should().Be("s1");
        result.Request.ItemId.Should().Be("/x");
    }

    [Theory]
    [InlineData("eventType")]
    [InlineData("sessionId")]
    [InlineData("itemId")]
    public void Create_FilterTooLong_Returns400(string which)
    {
        string big = new string('a', 257);
        TimelineRequestResult result = Create(
            eventType: which == "eventType" ? big : null,
            sessionId: which == "sessionId" ? big : null,
            itemId: which == "itemId" ? big : null);

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(TimelineRequestFactory.StatusBadRequest);
        result.ErrorMessage.Should().Contain("256");
    }

    [Fact]
    public void Create_MaxLengthFilter_IsAccepted()
    {
        TimelineRequestResult result = Create(eventType: new string('a', 256));

        result.Success.Should().BeTrue();
    }

    [Fact]
    public void Create_MetaFilter_RequiresBothKeyAndValue()
    {
        Create(metaKey: "plan").Request.MetaKey.Should().BeNull("a key without a value is not a usable filter");
        Create(metaValue: "pro").Request.MetaValue.Should().BeNull("a value without a key is not a usable filter");

        TimelineRequestResult both = Create(metaKey: "plan", metaValue: "pro");
        both.Request.MetaKey.Should().Be("plan");
        both.Request.MetaValue.Should().Be("pro");
    }

    [Theory]
    [InlineData("metaKey")]
    [InlineData("metaValue")]
    public void Create_MetaFilterTooLong_Returns400(string which)
    {
        string big = new string('m', 257);
        TimelineRequestResult result = Create(
            metaKey: which == "metaKey" ? big : "k",
            metaValue: which == "metaValue" ? big : "v");

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(TimelineRequestFactory.StatusBadRequest);
    }
}

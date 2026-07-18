using FluentAssertions;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public class IngestValidationTests
{
    [Fact]
    public void ResolveProjectKey_PrefersHeaderOverQuery()
    {
        IngestValidation.ResolveProjectKey("header-key", "query-key").Should().Be("header-key");
    }

    [Fact]
    public void ResolveProjectKey_FallsBackToQuery_WhenHeaderMissing()
    {
        IngestValidation.ResolveProjectKey(null, "query-key").Should().Be("query-key");
        IngestValidation.ResolveProjectKey("", "query-key").Should().Be("query-key");
    }

    [Fact]
    public void ResolveProjectKey_ReturnsEmpty_WhenBothMissing()
    {
        IngestValidation.ResolveProjectKey(null, null).Should().BeEmpty();
        IngestValidation.ResolveProjectKey("", "").Should().BeEmpty();
    }

    [Fact]
    public void IsValidPayload_Null_ReturnsFalse()
    {
        IngestValidation.IsValidPayload(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("", "t", "s", "e")]
    [InlineData("p", "", "s", "e")]
    [InlineData("p", "t", "", "e")]
    [InlineData("p", "t", "s", "")]
    public void IsValidPayload_MissingRequiredField_ReturnsFalse(
        string projectId, string tenantId, string sessionId, string eventType)
    {
        GlobalTelemetryPayload payload = new GlobalTelemetryPayload
        {
            ProjectId = projectId,
            TenantId = tenantId,
            SessionId = sessionId,
            EventType = eventType
        };

        IngestValidation.IsValidPayload(payload).Should().BeFalse();
    }

    [Fact]
    public void IsValidPayload_AllRequiredFieldsPresent_ReturnsTrue()
    {
        GlobalTelemetryPayload payload = new GlobalTelemetryPayload
        {
            ProjectId = "p",
            TenantId = "t",
            SessionId = "s",
            EventType = "e"
        };

        IngestValidation.IsValidPayload(payload).Should().BeTrue();
    }

    [Fact]
    public void IsValidPayload_DoesNotRequireItemId()
    {
        GlobalTelemetryPayload payload = new GlobalTelemetryPayload
        {
            ProjectId = "p",
            TenantId = "t",
            SessionId = "s",
            EventType = "e",
            ItemId = null
        };

        IngestValidation.IsValidPayload(payload).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, 1000, false)]
    [InlineData(500L, 1000, false)]
    [InlineData(1000L, 1000, false)]
    [InlineData(1001L, 1000, true)]
    public void ExceedsBodyLimit_ComparesContentLength(long? contentLength, long max, bool expected)
    {
        IngestValidation.ExceedsBodyLimit(contentLength, max).Should().Be(expected);
    }
}

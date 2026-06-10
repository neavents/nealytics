using FluentAssertions;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Tests.Shared.Base;

namespace Nealytics.Engine.Tests.Unit;

public class GlobalTelemetryPayloadTests : UnitTestBase
{
    [Fact]
    public void DefaultConstructor_EventId_IsNonEmptyGuid()
    {
        var payload = new GlobalTelemetryPayload();

        payload.EventId.Should().NotBeEmpty();
    }

    [Fact]
    public void DefaultConstructor_Timestamp_IsNearUtcNow()
    {
        var payload = new GlobalTelemetryPayload();
        var now = DateTime.UtcNow;

        payload.Timestamp.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DefaultConstructor_MetadataJson_IsEmptyObject()
    {
        var payload = new GlobalTelemetryPayload();

        payload.MetadataJson.Should().Be("{}");
    }

    [Fact]
    public void DefaultConstructor_ItemId_IsNull()
    {
        var payload = new GlobalTelemetryPayload();

        payload.ItemId.Should().BeNull();
    }

    [Fact]
    public void InitOnlyProperties_CanBeSetViaInitializer()
    {
        var payload = new GlobalTelemetryPayload
        {
            ProjectId = "proj-123",
            TenantId = "tenant-abc",
            SessionId = "sess-xyz",
            EventType = "page_view",
            ItemId = "item-456"
        };

        payload.ProjectId.Should().Be("proj-123");
        payload.TenantId.Should().Be("tenant-abc");
        payload.SessionId.Should().Be("sess-xyz");
        payload.EventType.Should().Be("page_view");
        payload.ItemId.Should().Be("item-456");
    }

    [Fact]
    public void SettableProperties_CanBeMutatedAfterConstruction()
    {
        var payload = new GlobalTelemetryPayload();
        var newGuid = Guid.NewGuid();
        var newTimestamp = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        payload.EventId = newGuid;
        payload.MetadataJson = "{\"key\":\"value\"}";
        payload.Timestamp = newTimestamp;

        payload.EventId.Should().Be(newGuid);
        payload.MetadataJson.Should().Be("{\"key\":\"value\"}");
        payload.Timestamp.Should().Be(newTimestamp);
    }
}

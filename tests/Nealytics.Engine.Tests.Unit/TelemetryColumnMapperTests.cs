using FluentAssertions;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public class TelemetryColumnMapperTests
{
    private static (Guid[], string[], string[], string[], string[], string?[], string[], DateTimeOffset[]) Alloc(int n)
        => (new Guid[n], new string[n], new string[n], new string[n], new string[n], new string?[n], new string[n], new DateTimeOffset[n]);

    [Fact]
    public void Fill_MapsEveryColumn_IncludingNullItemIdAndUtcTimestamp()
    {
        Guid id0 = Guid.NewGuid();
        List<GlobalTelemetryPayload> batch = new List<GlobalTelemetryPayload>
        {
            new GlobalTelemetryPayload
            {
                EventId = id0, ProjectId = "p0", TenantId = "t0", SessionId = "s0",
                EventType = "click", ItemId = null, MetadataJson = "{\"a\":1}",
                Timestamp = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Unspecified)
            },
            new GlobalTelemetryPayload
            {
                EventId = Guid.NewGuid(), ProjectId = "p1", TenantId = "t1", SessionId = "s1",
                EventType = "view", ItemId = "/home", MetadataJson = "{}",
                Timestamp = new DateTime(2026, 4, 2, 11, 0, 0, DateTimeKind.Utc)
            }
        };

        var (eventIds, projectIds, tenantIds, sessionIds, eventTypes, itemIds, metadataJsons, timestamps) = Alloc(2);

        TelemetryColumnMapper.Fill(batch, 2, eventIds, projectIds, tenantIds, sessionIds, eventTypes, itemIds, metadataJsons, timestamps);

        eventIds[0].Should().Be(id0);
        projectIds[0].Should().Be("p0");
        tenantIds[1].Should().Be("t1");
        sessionIds[0].Should().Be("s0");
        eventTypes[1].Should().Be("view");
        itemIds[0].Should().BeNull();
        itemIds[1].Should().Be("/home");
        metadataJsons[0].Should().Be("{\"a\":1}");
        timestamps[0].Offset.Should().Be(TimeSpan.Zero);
        timestamps[0].UtcDateTime.Should().Be(new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Fill_RespectsCount_AndIgnoresTrailingSlots()
    {
        List<GlobalTelemetryPayload> batch = new List<GlobalTelemetryPayload>
        {
            new GlobalTelemetryPayload { ProjectId = "only", TenantId = "t", SessionId = "s", EventType = "e" }
        };

        var (eventIds, projectIds, tenantIds, sessionIds, eventTypes, itemIds, metadataJsons, timestamps) = Alloc(4);

        TelemetryColumnMapper.Fill(batch, 1, eventIds, projectIds, tenantIds, sessionIds, eventTypes, itemIds, metadataJsons, timestamps);

        projectIds[0].Should().Be("only");
        projectIds[1].Should().BeNull("only the first slot should be populated");
    }

    [Fact]
    public void BuildColumns_ContainsAllEightColumns_SlicedToCount()
    {
        var (eventIds, projectIds, tenantIds, sessionIds, eventTypes, itemIds, metadataJsons, timestamps) = Alloc(10);

        Dictionary<string, object?> columns = TelemetryColumnMapper.BuildColumns(
            3, eventIds, projectIds, tenantIds, sessionIds, eventTypes, itemIds, metadataJsons, timestamps);

        columns.Keys.Should().BeEquivalentTo(new[]
        {
            "event_id", "project_id", "tenant_id", "session_id", "event_type", "item_id", "metadata_json", "timestamp"
        });
        ((ArraySegment<Guid>)columns["event_id"]!).Count.Should().Be(3);
        ((ArraySegment<string>)columns["project_id"]!).Count.Should().Be(3);
        ((ArraySegment<DateTimeOffset>)columns["timestamp"]!).Count.Should().Be(3);
    }
}

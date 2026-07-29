using FluentAssertions;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// Ported from TelemetryColumnMapperTests when the pooled column arrays moved behind
/// <see cref="TelemetryColumnBuffers"/>. The original assertions are kept and extended to the
/// dimensions promoted out of <c>metadata_json</c> — menu, section and table — plus the
/// edge-derived device attributes.
/// </summary>
public class TelemetryColumnBuffersTests
{
    [Fact]
    public void Fill_MapsEveryColumn_IncludingNullItemIdAndUtcTimestamp()
    {
        Guid id0 = Guid.NewGuid();
        List<GlobalTelemetryPayload> batch =
        [
            new GlobalTelemetryPayload
            {
                EventId = id0, ProjectId = "p0", TenantId = "t0", SessionId = "s0", UserId = "u0",
                EventType = "click", ItemId = null, MetadataJson = "{\"a\":1}",
                Timestamp = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Unspecified)
            },
            new GlobalTelemetryPayload
            {
                EventId = Guid.NewGuid(), ProjectId = "p1", TenantId = "t1", SessionId = "s1", UserId = null,
                EventType = "view", ItemId = "/home", MetadataJson = "{}",
                Timestamp = new DateTime(2026, 4, 2, 11, 0, 0, DateTimeKind.Utc)
            }
        ];

        using TelemetryColumnBuffers buffers = new(2);
        buffers.Fill(batch);

        buffers.EventIds[0].Should().Be(id0);
        buffers.ProjectIds[0].Should().Be("p0");
        buffers.TenantIds[1].Should().Be("t1");
        buffers.SessionIds[0].Should().Be("s0");
        buffers.UserIds[0].Should().Be("u0");
        buffers.UserIds[1].Should().BeNull("an anonymous event carries a NULL user_id");
        buffers.EventTypes[1].Should().Be("view");
        buffers.ItemIds[0].Should().BeNull();
        buffers.ItemIds[1].Should().Be("/home");
        buffers.MetadataJsons[0].Should().Be("{\"a\":1}");
        buffers.Timestamps[0].Offset.Should().Be(TimeSpan.Zero);
        buffers.Timestamps[0].UtcDateTime.Should().Be(new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Fill_MapsThePromotedDimensions()
    {
        List<GlobalTelemetryPayload> batch =
        [
            new GlobalTelemetryPayload
            {
                ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "section_open",
                MenuId = "01MENU", SectionId = "42", TableId = "01TABLE",
                DeviceClass = "mobile", Os = "iOS", Browser = "Safari", Country = "TR",
            }
        ];

        using TelemetryColumnBuffers buffers = new(1);
        buffers.Fill(batch);

        buffers.MenuIds[0].Should().Be("01MENU", "per-menu analytics groups on this column");
        buffers.SectionIds[0].Should().Be("42");
        buffers.TableIds[0].Should().Be("01TABLE", "table QR scans resolve to a table at the edge");
        buffers.DeviceClasses[0].Should().Be("mobile");
        buffers.OperatingSystems[0].Should().Be("iOS");
        buffers.Browsers[0].Should().Be("Safari");
        buffers.Countries[0].Should().Be("TR");
    }

    [Fact]
    public void Fill_LeavesLowCardinalityColumnsEmptyRatherThanNull()
    {
        // ClickHouse LowCardinality(String) is non-nullable here; a null would break the insert,
        // and grouping on it should show an "unknown" bucket rather than dropping the row.
        List<GlobalTelemetryPayload> batch =
        [
            new GlobalTelemetryPayload { ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e" }
        ];

        using TelemetryColumnBuffers buffers = new(1);
        buffers.Fill(batch);

        buffers.DeviceClasses[0].Should().BeEmpty();
        buffers.Countries[0].Should().BeEmpty();
        buffers.MenuIds[0].Should().BeNull("identifier columns stay nullable");
    }

    [Fact]
    public void Fill_RespectsCount_AndIgnoresTrailingSlots()
    {
        List<GlobalTelemetryPayload> batch =
        [
            new GlobalTelemetryPayload { ProjectId = "only", TenantId = "t", SessionId = "s", EventType = "e" }
        ];

        using TelemetryColumnBuffers buffers = new(1);
        buffers.Fill(batch);

        buffers.ProjectIds[0].Should().Be("only");
        buffers.UserIds[0].Should().BeNull("the single event has no user_id");
    }

    [Fact]
    public void BuildColumns_NamesMatchTheInsertStatement_SlicedToCount()
    {
        using TelemetryColumnBuffers buffers = new(3);
        Dictionary<string, object?> columns = buffers.BuildColumns();

        // The driver binds by name — a column present here but absent from the INSERT (or the
        // reverse) rejects the whole batch, so assert the two are the same set.
        string statement = ClickHouseBatchWriter.InsertColumns;
        string inner = statement[(statement.IndexOf('(') + 1)..statement.LastIndexOf(')')];
        string[] declared = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        columns.Keys.Should().BeEquivalentTo(declared);

        ((ArraySegment<Guid>)columns["event_id"]!).Count.Should().Be(3);
        ((ArraySegment<string>)columns["project_id"]!).Count.Should().Be(3);
        ((ArraySegment<string?>)columns["menu_id"]!).Count.Should().Be(3);
        ((ArraySegment<string>)columns["device_class"]!).Count.Should().Be(3);
        ((ArraySegment<DateTimeOffset>)columns["timestamp"]!).Count.Should().Be(3);
    }
}

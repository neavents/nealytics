using FluentAssertions;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// Ported from TelemetryColumnMapperTests when the pooled column arrays moved behind
/// <see cref="TelemetryColumnBuffers"/>, then again when the deployment's dimensions stopped being
/// fields on this class and became registry-driven buffers. The original assertions are kept.
///
/// The dimension names used here are the test's own vocabulary, not the engine's — that is the
/// point. Nothing in <c>src/</c> knows them.
/// </summary>
public class TelemetryColumnBuffersTests
{
    private static TelemetryColumnLayout Layout(params (string Name, string Type)[] dimensions)
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [.. dimensions.Select(d => new DimensionOptions { Name = d.Name, Type = d.Type })],
        };

        return new TelemetryColumnLayout(new DimensionRegistry(options));
    }

    [Fact]
    public void Fill_MapsEveryColumn_IncludingNullObjectIdAndUtcTimestamp()
    {
        Guid id0 = Guid.NewGuid();
        List<GlobalTelemetryPayload> batch =
        [
            new GlobalTelemetryPayload
            {
                EventId = id0, ProjectId = "p0", TenantId = "t0", SessionId = "s0", UserId = "u0",
                EventType = "click", ObjectId = null, MetadataJson = "{\"a\":1}",
                Timestamp = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Unspecified)
            },
            new GlobalTelemetryPayload
            {
                EventId = Guid.NewGuid(), ProjectId = "p1", TenantId = "t1", SessionId = "s1", UserId = null,
                EventType = "view", ObjectId = "/home", MetadataJson = "{}",
                Timestamp = new DateTime(2026, 4, 2, 11, 0, 0, DateTimeKind.Utc)
            }
        ];

        using TelemetryColumnBuffers buffers = new(2, Layout());
        buffers.Fill(batch);

        buffers.EventIds[0].Should().Be(id0);
        buffers.ProjectIds[0].Should().Be("p0");
        buffers.TenantIds[1].Should().Be("t1");
        buffers.SessionIds[0].Should().Be("s0");
        buffers.UserIds[0].Should().Be("u0");
        buffers.UserIds[1].Should().BeNull("an anonymous event carries a NULL user_id");
        buffers.EventTypes[1].Should().Be("view");
        buffers.ObjectIds[0].Should().BeNull();
        buffers.ObjectIds[1].Should().Be("/home");
        buffers.MetadataJsons[0].Should().Be("{\"a\":1}");
        buffers.Timestamps[0].Offset.Should().Be(TimeSpan.Zero);
        buffers.Timestamps[0].UtcDateTime.Should().Be(new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Fill_MapsDeclaredDimensions_FromTheGenericMap()
    {
        TelemetryColumnLayout layout = Layout(("widget_id", "String"), ("shelf_id", "String"));

        List<GlobalTelemetryPayload> batch =
        [
            new GlobalTelemetryPayload
            {
                ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "shelf_open",
                Dimensions = new Dictionary<string, string>
                {
                    ["widget_id"] = "01ARZ3NDEKTSV4RRFFQ69G5FAV",
                    ["shelf_id"] = "01ARZ3NDEKTSV4RRFFQ69G5FB0",
                },
                DeviceClass = "mobile", Os = "iOS", Browser = "Safari", Country = "TR",
            }
        ];

        using TelemetryColumnBuffers buffers = new(1, layout);
        buffers.Fill(batch);

        Dictionary<string, object?> columns = buffers.BuildColumns();

        ((ArraySegment<string?>)columns["widget_id"]!)[0].Should().Be("01ARZ3NDEKTSV4RRFFQ69G5FAV");
        ((ArraySegment<string?>)columns["shelf_id"]!)[0].Should().Be("01ARZ3NDEKTSV4RRFFQ69G5FB0");
        buffers.DeviceClasses[0].Should().Be("mobile");
        buffers.OperatingSystems[0].Should().Be("iOS");
        buffers.Browsers[0].Should().Be("Safari");
        buffers.Countries[0].Should().Be("TR");
    }

    [Fact]
    public void Fill_IgnoresPayloadKeysThatAreNotDeclared()
    {
        // A WAL record written before a dimension was retired still carries its key. The buffers
        // walk the registry, not the payload, so the stale key cannot widen the insert to a column
        // that may no longer be written.
        TelemetryColumnLayout layout = Layout(("widget_id", "String"));

        List<GlobalTelemetryPayload> batch =
        [
            new GlobalTelemetryPayload
            {
                ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e",
                Dimensions = new Dictionary<string, string>
                {
                    ["widget_id"] = "kept",
                    ["retired_id"] = "dropped",
                },
            }
        ];

        using TelemetryColumnBuffers buffers = new(1, layout);
        buffers.Fill(batch);

        Dictionary<string, object?> columns = buffers.BuildColumns();

        columns.Should().ContainKey("widget_id");
        columns.Should().NotContainKey("retired_id",
            "an undeclared key must not become a column in the INSERT");
    }

    [Fact]
    public void Fill_LeavesLowCardinalityColumnsEmptyRatherThanNull()
    {
        // ClickHouse LowCardinality(String) is non-nullable here; a null would break the insert,
        // and grouping on it should show an "unknown" bucket rather than dropping the row.
        TelemetryColumnLayout layout = Layout(("plan_tier", "LowCardinality"), ("widget_id", "String"));

        List<GlobalTelemetryPayload> batch =
        [
            new GlobalTelemetryPayload { ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e" }
        ];

        using TelemetryColumnBuffers buffers = new(1, layout);
        buffers.Fill(batch);

        Dictionary<string, object?> columns = buffers.BuildColumns();

        buffers.DeviceClasses[0].Should().BeEmpty();
        buffers.Countries[0].Should().BeEmpty();
        ((ArraySegment<string>)columns["plan_tier"]!)[0].Should()
            .BeEmpty("a LowCardinality dimension uses empty string so GROUP BY stays total");
        ((ArraySegment<string?>)columns["widget_id"]!)[0].Should()
            .BeNull("identifier dimensions stay nullable");
    }

    [Fact]
    public void Fill_TreatsEmptyDimensionValueAsAbsent()
    {
        TelemetryColumnLayout layout = Layout(("widget_id", "String"));

        List<GlobalTelemetryPayload> batch =
        [
            new GlobalTelemetryPayload
            {
                ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e",
                Dimensions = new Dictionary<string, string> { ["widget_id"] = "" },
            }
        ];

        using TelemetryColumnBuffers buffers = new(1, layout);
        buffers.Fill(batch);

        Dictionary<string, object?> columns = buffers.BuildColumns();

        ((ArraySegment<string?>)columns["widget_id"]!)[0].Should()
            .BeNull("an empty string would otherwise become its own GROUP BY bucket meaning 'absent'");
    }

    [Theory]
    [InlineData("UInt64", "not-a-number")]
    [InlineData("Int64", "12.5")]
    [InlineData("DateTime", "yesterday")]
    public void Fill_CountsValuesThatDoNotParseAsTheDeclaredType(string type, string badValue)
    {
        TelemetryColumnLayout layout = Layout(("measure", type));

        List<GlobalTelemetryPayload> batch =
        [
            new GlobalTelemetryPayload
            {
                ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e",
                Dimensions = new Dictionary<string, string> { ["measure"] = badValue },
            }
        ];

        using TelemetryColumnBuffers buffers = new(1, layout);
        buffers.Fill(batch);

        buffers.DimensionBuffers[0].RejectedValueCount.Should().Be(1,
            "a value that arrived and did not survive must be counted, not discarded quietly");
    }

    [Theory]
    [InlineData("UInt64", "42")]
    [InlineData("Int64", "-42")]
    public void Fill_AcceptsWellFormedNumericDimensions(string type, string value)
    {
        TelemetryColumnLayout layout = Layout(("measure", type));

        List<GlobalTelemetryPayload> batch =
        [
            new GlobalTelemetryPayload
            {
                ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e",
                Dimensions = new Dictionary<string, string> { ["measure"] = value },
            }
        ];

        using TelemetryColumnBuffers buffers = new(1, layout);
        buffers.Fill(batch);

        buffers.DimensionBuffers[0].RejectedValueCount.Should().Be(0);
    }

    [Fact]
    public void Fill_RespectsCount_AndIgnoresTrailingSlots()
    {
        List<GlobalTelemetryPayload> batch =
        [
            new GlobalTelemetryPayload { ProjectId = "only", TenantId = "t", SessionId = "s", EventType = "e" }
        ];

        using TelemetryColumnBuffers buffers = new(1, Layout());
        buffers.Fill(batch);

        buffers.ProjectIds[0].Should().Be("only");
        buffers.UserIds[0].Should().BeNull("the single event has no user_id");
    }

    [Fact]
    public void BuildColumns_NamesMatchTheInsertStatement_InTheSameOrder()
    {
        TelemetryColumnLayout layout = Layout(("widget_id", "String"), ("plan_tier", "LowCardinality"));

        using TelemetryColumnBuffers buffers = new(3, layout);
        Dictionary<string, object?> columns = buffers.BuildColumns();

        // The driver binds by name — a column present here but absent from the INSERT (or the
        // reverse) rejects the whole batch. Order is asserted too: the INSERT column list and the
        // value arrays are paired positionally by the layout, and a misalignment does not throw.
        string statement = ClickHouseBatchWriter.BuildInsertColumns(layout);
        string inner = statement[(statement.IndexOf('(') + 1)..statement.LastIndexOf(')')];
        string[] declared = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // The INSERT must be built from the layout's one list.
        declared.Should().Equal(layout.ColumnNames);
        columns.Keys.Should().BeEquivalentTo(declared);

        ((ArraySegment<Guid>)columns["event_id"]!).Count.Should().Be(3);
        ((ArraySegment<string>)columns["project_id"]!).Count.Should().Be(3);
        ((ArraySegment<string?>)columns["object_id"]!).Count.Should().Be(3);
        ((ArraySegment<string?>)columns["widget_id"]!).Count.Should().Be(3);
        ((ArraySegment<string>)columns["plan_tier"]!).Count.Should().Be(3);
        ((ArraySegment<string>)columns["device_class"]!).Count.Should().Be(3);
        ((ArraySegment<DateTimeOffset>)columns["timestamp"]!).Count.Should().Be(3);
    }

    [Fact]
    public void BuildColumns_PlacesDimensionsAfterCoreColumns_InRegistryOrder()
    {
        // Registry order is config order. Stable order is what keeps the name list and the value
        // list pairable; two iterations of an unordered collection is how tenant ids end up in the
        // browser column without anything throwing.
        TelemetryColumnLayout layout = Layout(("zzz_last", "String"), ("aaa_first", "String"));

        layout.ColumnNames.Take(TelemetryColumnLayout.CoreColumns.Count)
            .Should().Equal(TelemetryColumnLayout.CoreColumns);
        layout.ColumnNames.Skip(TelemetryColumnLayout.CoreColumns.Count)
            .Should().Equal(["zzz_last", "aaa_first"]);
    }
}

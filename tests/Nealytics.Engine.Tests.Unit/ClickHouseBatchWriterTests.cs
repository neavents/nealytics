using FluentAssertions;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public class ClickHouseBatchWriterTests
{
    private static TelemetryColumnLayout Layout(params (string Name, string Type)[] dimensions)
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [.. dimensions.Select(d => new DimensionOptions { Name = d.Name, Type = d.Type })],
        };

        DimensionRegistry registry = new(options);
        return new TelemetryColumnLayout(registry, new MeasureRegistry(options, registry));
    }

    private static TelemetryColumnLayout LayoutWith(
        (string Name, string Type)[] dimensions,
        (string Name, string Type)[] measures)
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [.. dimensions.Select(d => new DimensionOptions { Name = d.Name, Type = d.Type })],
            Measures = [.. measures.Select(m => new MeasureOptions { Name = m.Name, Type = m.Type })],
        };

        DimensionRegistry registry = new(options);
        return new TelemetryColumnLayout(registry, new MeasureRegistry(options, registry));
    }

    [Fact]
    public void BuildInsertCommand_WithAsyncInsert_AppendsWaitForAsyncSettings()
    {
        string command = ClickHouseBatchWriter.BuildInsertCommand(Layout(), asyncInsert: true);

        command.Should().Contain("INSERT INTO nealytics_core.global_events");
        command.Should().Contain("SETTINGS async_insert=1, wait_for_async_insert=1");
        command.Should().EndWith("VALUES");
        command.IndexOf("SETTINGS", System.StringComparison.Ordinal)
            .Should().BeLessThan(command.IndexOf("VALUES", System.StringComparison.Ordinal),
                "the SETTINGS clause must precede VALUES for a valid ClickHouse INSERT");
    }

    [Fact]
    public void BuildInsertCommand_WithoutAsyncInsert_HasNoSettingsClause()
    {
        string command = ClickHouseBatchWriter.BuildInsertCommand(Layout(), asyncInsert: false);

        command.Should().NotContain("SETTINGS");
        command.Should().NotContain("async_insert");
        command.Should().EndWith("VALUES");
    }

    [Fact]
    public void BuildInsertCommand_WithNoDeclaredDimensions_IsCoreColumnsOnly()
    {
        string command = ClickHouseBatchWriter.BuildInsertCommand(Layout(), asyncInsert: true);

        // The shipped engine declares nothing. object_id is "the thing this event is about";
        // device_class/os/browser/country are derived at the edge from the User-Agent and
        // the edge's request metadata. No deployment vocabulary appears here at all.
        command.Should().Contain("(event_id, project_id, tenant_id, session_id, user_id, event_type, object_id, seq, traffic_class, page_path, referrer, ingested_at, device_class, os, browser, country, metadata_json, timestamp)");
    }

    [Fact]
    public void BuildInsertCommand_AppendsDeclaredDimensions_AfterTheCoreColumns()
    {
        string command = ClickHouseBatchWriter.BuildInsertCommand(
            Layout(("widget_id", "String"), ("plan_tier", "LowCardinality")), asyncInsert: false);

        command.Should().Contain(
            "(event_id, project_id, tenant_id, session_id, user_id, event_type, object_id, seq, traffic_class, page_path, referrer, ingested_at, device_class, os, browser, country, metadata_json, timestamp, widget_id, plan_tier)");
    }

    [Fact]
    public void BuildInsertColumns_ContainsNoCompiledInDomainVocabulary()
    {
        // The whole point of the exercise: with nothing declared, the statement the engine emits
        // cannot name anyone's product concepts, because it does not know any.
        string columns = ClickHouseBatchWriter.BuildInsertColumns(Layout());

        columns.Should().NotContainEquivalentOf("article");
        columns.Should().NotContainEquivalentOf("section");
        columns.Should().NotContainEquivalentOf("item_id");
    }
}

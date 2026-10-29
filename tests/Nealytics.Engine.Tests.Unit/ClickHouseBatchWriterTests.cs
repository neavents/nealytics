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


public sealed class BatchWriterRejectionDiagnosticsTests
{
    [Fact]
    public async Task RejectedCellsReportTheirDeclaredNamesCountsAndTypesBeforeStorageAcquisition()
    {
        var settings = new TelemetryEngineOptions
        {
            ClickHouseConnectionString = "Host=127.0.0.1;Port=9;Database=nealytics_core;User=default;Password=;",
            ConnectionAcquireTimeoutSeconds = 1,
            Dimensions =
            [
                new DimensionOptions { Name = "numeric_d", Type = "Int64" },
                new DimensionOptions { Name = "text_d", Type = "String" }
            ],
            Measures =
            [
                new MeasureOptions { Name = "numeric_m", Type = "Float64", Minimum = 0, Maximum = 10 },
                new MeasureOptions { Name = "valid_m", Type = "Float64" }
            ]
        };
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        var dimensions = new DimensionRegistry(settings);
        await using var factory = new Nealytics.Engine.Infrastructure.Storage.ClickHouseConnectionFactory(options);
        var logger = new RejectionLogger();
        var writer = new ClickHouseBatchWriter(factory, dimensions, new MeasureRegistry(settings, dimensions), options, logger);
        Nealytics.Engine.Infrastructure.Serialization.GlobalTelemetryPayload[] items =
        [
            new()
            {
                Dimensions = new() { ["numeric_d"] = "invalid", ["text_d"] = "text" },
                Measures = new() { ["numeric_m"] = "invalid", ["valid_m"] = "1" }
            },
            new()
            {
                Dimensions = new() { ["numeric_d"] = "42" },
                Measures = new() { ["numeric_m"] = "1000", ["valid_m"] = "2" }
            },
            new()
            {
                Dimensions = new() { ["numeric_d"] = "9" },
                Measures = new() { ["numeric_m"] = "4" }
            }
        ];
        await Assert.ThrowsAnyAsync<Exception>(() => writer.WriteAsync(items, 3, CancellationToken.None));
        Assert.Equal(2, logger.Entries.Count);
        Assert.Contains(logger.Entries, entry => entry.Id == 9101
            && entry.Message.Contains("numeric_d") && entry.Message.Contains("1 value(s)")
            && entry.Message.Contains("batch of 3") && entry.Message.Contains("Int64"));
        Assert.Contains(logger.Entries, entry => entry.Id == 9102
            && entry.Message.Contains("numeric_m") && entry.Message.Contains("2 value(s)")
            && entry.Message.Contains("batch of 3") && entry.Message.Contains("Float64"));
    }

    private sealed class RejectionLogger : Microsoft.Extensions.Logging.ILogger<ClickHouseBatchWriter>
    {
        public List<(int Id, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((eventId.Id, formatter(state, exception)));
    }
}

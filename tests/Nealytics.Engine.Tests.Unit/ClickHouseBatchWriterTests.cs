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

    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    public async Task DeliverySchemaIdentityDoesNotChangeWithTheProcessCulture(string culture)
    {
        var settings = new TelemetryEngineOptions
        {
            Dimensions = [new DimensionOptions { Name = "label", Type = "String" }],
            Measures = [new MeasureOptions { Name = "amount", Type = "Float64", Minimum = 1.25, Maximum = 9.75 }]
        };
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            string expected = await DeliveryFingerprint(settings);
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, await DeliveryFingerprint(settings));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(false, "5D40A065E5170FBA120EC79870C2B9FE89867A09F9B67C7D5404C1EA5E8B5E91")]
    [InlineData(true, "052EFED012A4FA4CCE78403228198C61CA098E291D46CF931B8FE891DE336A2B")]
    public async Task DeliverySchemaHasAVersionedStablePersistedIdentity(bool declared, string expected)
    {
        var settings = new TelemetryEngineOptions();
        if (declared)
        {
            settings.Dimensions = [new DimensionOptions { Name = "label", Type = "String" }];
            settings.Measures = [new MeasureOptions { Name = "amount", Type = "Float64", Minimum = 1.25, Maximum = 9.75 }];
        }
        Assert.Equal(expected, await DeliveryFingerprint(settings));
    }

    [Theory]
    [InlineData("dimension-name")]
    [InlineData("dimension-type")]
    [InlineData("dimension-order")]
    [InlineData("measure-name")]
    [InlineData("measure-type")]
    [InlineData("measure-order")]
    [InlineData("minimum")]
    [InlineData("maximum")]
    [InlineData("no-minimum")]
    [InlineData("no-maximum")]
    public async Task DeliverySchemaIdentityDetectsChangesToStoredColumnsAndConversionRules(string change)
    {
        var settings = new TelemetryEngineOptions
        {
            Dimensions =
            [
                new DimensionOptions { Name = "label", Type = "String" },
                new DimensionOptions { Name = "group", Type = "String" }
            ],
            Measures =
            [
                new MeasureOptions { Name = "amount", Type = "Float64", Minimum = 1.25, Maximum = 9.75 },
                new MeasureOptions { Name = "quantity", Type = "UInt64" }
            ]
        };
        string original = await DeliveryFingerprint(settings);
        switch (change)
        {
            case "dimension-name": settings.Dimensions[0].Name = "other_label"; break;
            case "dimension-type": settings.Dimensions[0].Type = "UInt64"; break;
            case "dimension-order": settings.Dimensions.Reverse(); break;
            case "measure-name": settings.Measures[0].Name = "other_amount"; break;
            case "measure-type": settings.Measures[0].Type = "Float32"; break;
            case "measure-order": settings.Measures.Reverse(); break;
            case "minimum": settings.Measures[0].Minimum = 2.25; break;
            case "maximum": settings.Measures[0].Maximum = 8.75; break;
            case "no-minimum": settings.Measures[0].Minimum = null; break;
            case "no-maximum": settings.Measures[0].Maximum = null; break;
        }
        Assert.NotEqual(original, await DeliveryFingerprint(settings));
    }

    private static async Task<string> DeliveryFingerprint(TelemetryEngineOptions settings)
    {
        var options = Microsoft.Extensions.Options.Options.Create(settings);
        await using var connections = new Nealytics.Engine.Infrastructure.Storage.ClickHouseConnectionFactory(options);
        var dimensions = new DimensionRegistry(settings);
        var writer = new ClickHouseBatchWriter(connections, dimensions, new MeasureRegistry(settings, dimensions), options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClickHouseBatchWriter>.Instance);
        return writer.SchemaFingerprint;
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

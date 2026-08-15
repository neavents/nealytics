using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public class MeasureSanitizerTests
{
    private sealed class CapturingLogger : ILogger<MeasureSanitizer>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private static MeasureSanitizer Build(ILogger<MeasureSanitizer> logger, params string[] declared)
    {
        TelemetryEngineOptions options = new()
        {
            Measures = [.. declared.Select(name => new MeasureOptions { Name = name, Type = "UInt32" })],
        };

        DimensionRegistry dimensions = new(options);
        return new MeasureSanitizer(new MeasureRegistry(options, dimensions), logger);
    }

    private static GlobalTelemetryPayload Payload(Dictionary<string, string>? measures) => new()
    {
        ProjectId = "proj",
        TenantId = "tenant",
        SessionId = "sess",
        EventType = "view",
        Measures = measures,
    };

    [Fact]
    public void DeclaredKeys_SurviveUntouched()
    {
        MeasureSanitizer sanitizer = Build(NullLogger<MeasureSanitizer>.Instance, "dwell_ms");
        GlobalTelemetryPayload payload = Payload(new Dictionary<string, string> { ["dwell_ms"] = "1200" });

        sanitizer.Sanitize(payload).Should().Be(0);
        payload.Measures.Should().ContainKey("dwell_ms").WhoseValue.Should().Be("1200");
    }

    [Fact]
    public void UndeclaredKey_IsDroppedButTheEventSurvives()
    {
        MeasureSanitizer sanitizer = Build(NullLogger<MeasureSanitizer>.Instance, "dwell_ms");
        GlobalTelemetryPayload payload = Payload(new Dictionary<string, string>
        {
            ["dwell_ms"] = "1200",
            ["typo_ms"] = "9",
        });

        sanitizer.Sanitize(payload).Should().Be(1);
        payload.Measures.Should().ContainKey("dwell_ms");
        payload.Measures.Should().NotContainKey("typo_ms",
            "the beacon cannot retry, so losing one field must never cost the whole event");
    }

    [Fact]
    public void DroppedKeyIsNamedInTheLog_WithTheProject()
    {
        CapturingLogger logger = new();
        MeasureSanitizer sanitizer = Build(logger, "dwell_ms");

        sanitizer.Sanitize(Payload(new Dictionary<string, string> { ["typo_ms"] = "9" }));

        logger.Messages.Should().ContainSingle()
            .Which.Should().Contain("typo_ms").And.Contain("proj").And.Contain("TelemetryEngine:Measures");
    }

    [Fact]
    public void RetiredMeasure_IsRefusedLikeAnUndeclaredOne()
    {
        TelemetryEngineOptions options = new()
        {
            Measures = [new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Retired = true }],
        };

        DimensionRegistry dimensions = new(options);
        MeasureSanitizer sanitizer = new(
            new MeasureRegistry(options, dimensions), NullLogger<MeasureSanitizer>.Instance);

        GlobalTelemetryPayload payload = Payload(new Dictionary<string, string> { ["dwell_ms"] = "1200" });

        sanitizer.Sanitize(payload).Should().Be(1);
        payload.Measures.Should().BeEmpty("a retired measure keeps its column but stops collecting");
    }

    [Fact]
    public void NoMeasures_IsNotAnError()
    {
        MeasureSanitizer sanitizer = Build(NullLogger<MeasureSanitizer>.Instance, "dwell_ms");

        sanitizer.Sanitize(Payload(null)).Should().Be(0);
    }

    [Fact]
    public void AllKeysUndeclared_StillKeepsTheEvent()
    {
        MeasureSanitizer sanitizer = Build(NullLogger<MeasureSanitizer>.Instance, "dwell_ms");
        GlobalTelemetryPayload payload = Payload(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });

        sanitizer.Sanitize(payload).Should().Be(2);
        payload.Measures.Should().BeEmpty();
        payload.EventType.Should().Be("view");
    }

    [Fact]
    public void ADeclaredDimensionNameIsNotADeclaredMeasure()
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [new DimensionOptions { Name = "menu_id", Type = "String" }],
            Measures = [new MeasureOptions { Name = "dwell_ms", Type = "UInt32" }],
        };

        DimensionRegistry dimensions = new(options);
        MeasureSanitizer sanitizer = new(
            new MeasureRegistry(options, dimensions), NullLogger<MeasureSanitizer>.Instance);

        GlobalTelemetryPayload payload = Payload(new Dictionary<string, string> { ["menu_id"] = "01ABC" });

        sanitizer.Sanitize(payload).Should().Be(1,
            "the two maps are separate kinds, and a dimension arriving in the measures map would "
            + "otherwise be parsed as a number and rejected one layer later");
    }
}

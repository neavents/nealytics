using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public class DimensionSanitizerTests
{
    private sealed class CapturingLogger : ILogger<DimensionSanitizer>
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

    private static DimensionSanitizer Build(ILogger<DimensionSanitizer> logger, params string[] declared)
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [.. declared.Select(name => new DimensionOptions { Name = name })],
        };

        return new DimensionSanitizer(new DimensionRegistry(options), logger);
    }

    private static GlobalTelemetryPayload Payload(Dictionary<string, string>? dimensions) => new()
    {
        ProjectId = "proj",
        TenantId = "tenant",
        SessionId = "sess",
        EventType = "view",
        Dimensions = dimensions,
    };

    [Fact]
    public void DeclaredKeys_SurviveUntouched()
    {
        DimensionSanitizer sanitizer = Build(NullLogger<DimensionSanitizer>.Instance, "widget_id");
        GlobalTelemetryPayload payload = Payload(new Dictionary<string, string> { ["widget_id"] = "01ABC" });

        sanitizer.Sanitize(payload).Should().Be(0);
        payload.Dimensions.Should().ContainKey("widget_id").WhoseValue.Should().Be("01ABC");
    }

    [Fact]
    public void UndeclaredKey_IsDropped_ButTheRestOfTheEventSurvives()
    {
        // Per field, never per event: the client is sendBeacon, which cannot retry and whose caller
        // cannot react. Losing a session's events over one misspelled key is worse than losing the
        // field.
        DimensionSanitizer sanitizer = Build(NullLogger<DimensionSanitizer>.Instance, "widget_id");
        GlobalTelemetryPayload payload = Payload(new Dictionary<string, string>
        {
            ["widget_id"] = "kept",
            ["mystery_id"] = "dropped",
        });

        sanitizer.Sanitize(payload).Should().Be(1);

        payload.Dimensions.Should().ContainKey("widget_id");
        payload.Dimensions.Should().NotContainKey("mystery_id");
        payload.EventType.Should().Be("view", "the event itself is never rejected for a bad key");
    }

    [Fact]
    public void UndeclaredKey_IsNamedInTheLog_WithTheProject()
    {
        // A dropped field that leaves no trace is the exact failure this design exists to end.
        CapturingLogger logger = new();
        DimensionSanitizer sanitizer = Build(logger, "widget_id");

        sanitizer.Sanitize(Payload(new Dictionary<string, string> { ["mystery_id"] = "x" }));

        logger.Messages.Should().ContainSingle()
            .Which.Should().Contain("mystery_id").And.Contain("proj");
    }

    [Fact]
    public void RetiredDimension_IsRefusedLikeAnyUndeclaredName()
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [new DimensionOptions { Name = "old_id", Retired = true }],
        };
        DimensionSanitizer sanitizer = new(
            new DimensionRegistry(options), NullLogger<DimensionSanitizer>.Instance);

        GlobalTelemetryPayload payload = Payload(new Dictionary<string, string> { ["old_id"] = "x" });

        sanitizer.Sanitize(payload).Should().Be(1);
        payload.Dimensions.Should().NotContainKey("old_id",
            "retiring stops collection; it does not keep accepting writes");
    }

    [Fact]
    public void NoDimensionsAtAll_IsNotAnError()
    {
        DimensionSanitizer sanitizer = Build(NullLogger<DimensionSanitizer>.Instance, "widget_id");

        sanitizer.Sanitize(Payload(null)).Should().Be(0);
        sanitizer.Sanitize(Payload([])).Should().Be(0);
    }

    [Fact]
    public void EveryKeyUndeclared_DropsThemAll_AndStillKeepsTheEvent()
    {
        DimensionSanitizer sanitizer = Build(NullLogger<DimensionSanitizer>.Instance);
        GlobalTelemetryPayload payload = Payload(new Dictionary<string, string>
        {
            ["a_id"] = "1",
            ["b_id"] = "2",
        });

        sanitizer.Sanitize(payload).Should().Be(2);
        payload.Dimensions.Should().BeEmpty();
        payload.SessionId.Should().Be("sess");
    }
}

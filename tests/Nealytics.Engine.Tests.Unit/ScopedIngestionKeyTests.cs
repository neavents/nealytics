using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public class ScopedIngestionKeyTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static TelemetryEngineOptions Options() => new()
    {
        AllowedProjectKeys = "legacy-key",
        IngestionKeys =
        [
            new IngestionKeyOptions { Key = "browser-key", Scope = "public" },
            new IngestionKeyOptions { Key = "pos-key", Scope = "server", ProjectId = "p", EventTypes = "sale.*,alias" },
            new IngestionKeyOptions { Key = "any-server-key", Scope = "Server" },
        ],
        ServerEventTypes = "sale.*,refund",
        Dimensions = [new DimensionOptions { Name = "currency", Type = "LowCardinality" }],
        Measures =
        [
            new MeasureOptions { Name = "amount", Type = "Decimal", Aggregations = "sum", ServerOnly = true, UnitDimension = "currency" },
            new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "sum" },
        ],
        AliasEventType = "alias",
    };

    private static ApiKeyValidator Keys(TelemetryEngineOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? Options()), NullLogger<ApiKeyValidator>.Instance);

    private static IngestGate Gate(TelemetryEngineOptions? options = null)
    {
        TelemetryEngineOptions engine = options ?? Options();
        return new IngestGate(engine, new MeasureRegistry(engine, new DimensionRegistry(engine)));
    }

    private static IngestionKeyPolicy Policy(string key)
    {
        Keys().TryResolve(key, out IngestionKeyPolicy? policy).Should().BeTrue();
        return policy!;
    }

    private static GlobalTelemetryPayload Event(
        string eventType = "page_view",
        string? sessionId = "s1",
        string trafficClass = "",
        Dictionary<string, string>? measures = null,
        DateTime? timestamp = null,
        string? userId = null,
        string? objectId = null) => new()
        {
            ProjectId = "p",
            TenantId = "t",
            SessionId = sessionId!,
            EventType = eventType,
            TrafficClass = trafficClass,
            Measures = measures,
            Timestamp = timestamp ?? Now,
            UserId = userId,
            ObjectId = objectId,
        };

    private static IngestRejection Check(string key, GlobalTelemetryPayload payload) =>
        Gate().Check(Policy(key), payload, Now, out _);

    [Fact]
    public void ALegacyKey_BehavesAsBeforeWhenNothingIsDeclaredServerOnly()
    {
        TelemetryEngineOptions options = new() { AllowedProjectKeys = "legacy-key" };
        IngestGate gate = Gate(options);
        Keys(options).TryResolve("legacy-key", out IngestionKeyPolicy? policy).Should().BeTrue();

        gate.Check(policy!, Event("sale.completed", trafficClass: "internal", timestamp: Now.AddDays(-30)), Now, out bool adjusted)
            .Should().Be(IngestRejection.None);
        adjusted.Should().BeFalse();
        gate.Check(policy!, Event(sessionId: ""), Now, out _).Should().Be(IngestRejection.MissingSessionId);
    }

    [Fact]
    public void APublicKey_CannotSendAServerEvent()
    {
        Check("browser-key", Event("sale.completed")).Should().Be(IngestRejection.EventTypeNotPermittedForKey);
        Check("browser-key", Event("refund")).Should().Be(IngestRejection.EventTypeNotPermittedForKey);
        Check("browser-key", Event("page_view")).Should().Be(IngestRejection.None);
    }

    [Fact]
    public void ALegacyKey_CannotSendAServerEventEither_OnceOneIsDeclared()
    {
        Check("legacy-key", Event("sale.completed")).Should().Be(IngestRejection.EventTypeNotPermittedForKey);
        Check("legacy-key", Event("page_view", trafficClass: "bot")).Should().Be(IngestRejection.None,
            "an unscoped key keeps every behaviour it had; only what is newly declared server-only is closed to it");
    }

    [Fact]
    public void AServerKey_MaySendServerEvents_WithoutASession()
    {
        Check("pos-key", Event("sale.completed", sessionId: null, measures: new() { ["amount"] = "12.50" }))
            .Should().Be(IngestRejection.None);
    }

    [Fact]
    public void AServerKey_IsLimitedToTheEventTypesItLists()
    {
        Check("pos-key", Event("page_view")).Should().Be(IngestRejection.EventTypeNotPermittedForKey);
        Check("any-server-key", Event("page_view", sessionId: null)).Should().Be(IngestRejection.None);
    }

    [Fact]
    public void AServerKey_StaysPinnedToItsProject()
    {
        GlobalTelemetryPayload elsewhere = new() { ProjectId = "q", TenantId = "t", SessionId = "s", EventType = "sale.x", Timestamp = Now };
        Check("pos-key", elsewhere).Should().Be(IngestRejection.ProjectNotPermittedForKey);
    }

    [Fact]
    public void AServerOnlyMeasure_IsRefusedFromAnythingButAServerKey()
    {
        Dictionary<string, string> forged = new() { ["amount"] = "1000" };

        Check("browser-key", Event(measures: forged)).Should().Be(IngestRejection.ServerOnlyField);
        Check("legacy-key", Event(measures: forged)).Should().Be(IngestRejection.ServerOnlyField);
        Check("browser-key", Event(measures: new() { ["dwell_ms"] = "10" })).Should().Be(IngestRejection.None);
    }

    [Theory]
    [InlineData("internal")]
    [InlineData("bot")]
    public void APublicKey_CannotClassifyItsOwnTraffic(string trafficClass)
    {
        Check("browser-key", Event(trafficClass: trafficClass)).Should().Be(IngestRejection.ServerOnlyField);
        Check("any-server-key", Event(trafficClass: trafficClass)).Should().Be(IngestRejection.None);
    }

    [Fact]
    public void APublicKey_CannotBackdate_TheServerReceiptTimeIsKept()
    {
        GlobalTelemetryPayload payload = Event(timestamp: Now.AddDays(-3));

        Gate().Check(Policy("browser-key"), payload, Now, out bool adjusted).Should().Be(IngestRejection.None);

        adjusted.Should().BeTrue();
        payload.Timestamp.Should().Be(Now);
    }

    [Fact]
    public void APublicKey_KeepsATimestampWithinTolerance()
    {
        GlobalTelemetryPayload payload = Event(timestamp: Now.AddSeconds(-90));

        Gate().Check(Policy("browser-key"), payload, Now, out bool adjusted);

        adjusted.Should().BeFalse();
        payload.Timestamp.Should().Be(Now.AddSeconds(-90));
    }

    [Fact]
    public void AServerKey_MayBackdate()
    {
        GlobalTelemetryPayload payload = Event("sale.completed", timestamp: Now.AddDays(-3));

        Gate().Check(Policy("pos-key"), payload, Now, out bool adjusted);

        adjusted.Should().BeFalse();
        payload.Timestamp.Should().Be(Now.AddDays(-3));
    }

    [Fact]
    public void AnAliasWithoutAnIdentity_IsRefused()
    {
        Check("pos-key", Event("alias")).Should().Be(IngestRejection.AliasWithoutIdentity);
        Check("pos-key", Event("alias", userId: "u1")).Should().Be(IngestRejection.None);
        Check("pos-key", Event("alias", objectId: "order-9")).Should().Be(IngestRejection.None);
    }

    [Fact]
    public void ScopedKeys_AreValidKeys()
    {
        ApiKeyValidator keys = Keys();

        keys.IsValid("browser-key").Should().BeTrue();
        keys.IsValid("legacy-key").Should().BeTrue();
        keys.IsServerKey("pos-key").Should().BeTrue();
        keys.IsServerKey("legacy-key").Should().BeFalse();
        keys.IsValid(null).Should().BeFalse();
    }

    [Fact]
    public void AnUnknownScope_RefusesTheBoot()
    {
        TelemetryEngineOptions options = Options();
        options.IngestionKeys[0].Scope = "admin";

        Action act = () => Keys(options);
        act.Should().Throw<InvalidOperationException>().WithMessage("*must be public or server*");
    }

    [Fact]
    public void AKeyDeclaredTwice_RefusesTheBoot()
    {
        TelemetryEngineOptions options = Options();
        options.IngestionKeys.Add(new IngestionKeyOptions { Key = "legacy-key", Scope = "server" });

        Action act = () => Keys(options);
        act.Should().Throw<InvalidOperationException>().WithMessage("*already declared*");
    }

    [Theory]
    [InlineData("sale.*,*bad")]
    [InlineData("a b")]
    public void AMalformedEventTypePattern_RefusesTheBoot(string pattern)
    {
        TelemetryEngineOptions options = Options();
        options.ServerEventTypes = pattern;

        Action act = () => Gate(options);
        act.Should().Throw<InvalidOperationException>().WithMessage("*TelemetryEngine:ServerEventTypes*");
    }

    [Fact]
    public void EventTypePatterns_MatchExactNamesAndPrefixes()
    {
        EventTypePattern pattern = EventTypePattern.Parse("sale.*,refund", "x");

        pattern.Matches("sale.completed").Should().BeTrue();
        pattern.Matches("refund").Should().BeTrue();
        pattern.Matches("refunds").Should().BeFalse();
        pattern.Matches("salesman").Should().BeFalse();
        EventTypePattern.Parse("*", "x").Matches("anything").Should().BeTrue();
        EventTypePattern.Parse("", "x").IsEmpty.Should().BeTrue();
    }
}

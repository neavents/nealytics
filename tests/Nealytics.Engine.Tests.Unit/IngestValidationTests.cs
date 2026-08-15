using FluentAssertions;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// What the ingest contract actually is.
///
/// It used to be four presence checks, and everything else about an event was whatever the caller
/// sent. That matters more here than in most services because two of the caller-supplied fields are
/// structural: <c>event_id</c> is part of the ReplacingMergeTree sort key, so a reused one collapses
/// a different row on merge, and <c>timestamp</c> is the partition key, so a broken clock creates a
/// partition retention will never reach.
///
/// The direction of the clock check is deliberate and asymmetric. A device hours behind is ordinary
/// and a backfill is legitimate, so the past is accepted without limit. Only the far future is
/// refused, because nothing legitimate is there.
/// </summary>
public class IngestValidationTests
{
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    private static GlobalTelemetryPayload Payload(
        string projectId = "p",
        string tenantId = "t",
        string sessionId = "s",
        string eventType = "e",
        string? objectId = null,
        string? userId = null,
        DateTime? timestamp = null) =>
        new()
        {
            ProjectId = projectId,
            TenantId = tenantId,
            SessionId = sessionId,
            EventType = eventType,
            ObjectId = objectId,
            UserId = userId,
            Timestamp = timestamp ?? Now,
        };

    [Fact]
    public void AWellFormedEventIsAccepted()
    {
        IngestValidation.Validate(Payload(), Now).Should().Be(IngestRejection.None);
    }

    [Theory]
    [InlineData("", "t", "s", "e", IngestRejection.MissingProjectId)]
    [InlineData("p", "", "s", "e", IngestRejection.MissingTenantId)]
    [InlineData("p", "t", "", "e", IngestRejection.MissingSessionId)]
    [InlineData("p", "t", "s", "", IngestRejection.MissingEventType)]
    public void EachRequiredFieldIsNamedWhenItIsMissing(
        string projectId, string tenantId, string sessionId, string eventType, IngestRejection expected)
    {
        // Named individually rather than reported as "invalid", because the caller is usually a
        // deployment wiring this up for the first time and "which of the four" is the whole answer.
        IngestValidation.Validate(Payload(projectId, tenantId, sessionId, eventType), Now)
            .Should().Be(expected);
    }

    [Fact]
    public void ANullPayloadIsRefusedRatherThanThrowing()
    {
        IngestValidation.Validate(null, Now).Should().NotBe(IngestRejection.None);
    }

    public static TheoryData<string> OverlongIdentifiers() => new()
    {
        "projectId", "tenantId", "sessionId", "objectId", "userId",
    };

    [Theory]
    [MemberData(nameof(OverlongIdentifiers))]
    public void AnOverlongIdentifierIsRefused(string field)
    {
        string tooLong = new('x', IngestValidation.MaxIdentifierLength + 1);

        GlobalTelemetryPayload payload = field switch
        {
            "projectId" => Payload(projectId: tooLong),
            "tenantId" => Payload(tenantId: tooLong),
            "sessionId" => Payload(sessionId: tooLong),
            "objectId" => Payload(objectId: tooLong),
            _ => Payload(userId: tooLong),
        };

        IngestValidation.Validate(payload, Now).Should().Be(IngestRejection.FieldTooLong);
    }

    [Fact]
    public void AnIdentifierExactlyAtTheLimitIsAccepted()
    {
        // The boundary in the accepting direction, because an off-by-one here silently refuses the
        // longest legitimate id a deployment has rather than an abusive one.
        IngestValidation.Validate(
            Payload(sessionId: new string('x', IngestValidation.MaxIdentifierLength)), Now)
            .Should().Be(IngestRejection.None);
    }

    [Fact]
    public void AnOverlongEventTypeIsRefused()
    {
        // event_type leads the sort key after project and tenant. An unbounded one is a very wide
        // sort key on every row in the table, not just on the offending one.
        IngestValidation.Validate(
            Payload(eventType: new string('x', IngestValidation.MaxEventTypeLength + 1)), Now)
            .Should().Be(IngestRejection.FieldTooLong);
    }

    [Fact]
    public void AnEventFromTheFarFutureIsRefused()
    {
        IngestValidation.Validate(Payload(timestamp: Now.AddDays(400)), Now)
            .Should().Be(IngestRejection.TimestampTooFarAhead);
    }

    [Fact]
    public void AnEventFromTheDistantPastIsAccepted()
    {
        // Asymmetric on purpose: a backfill is legitimate, and a phone whose clock is behind is
        // ordinary. Refusing the past would discard real events to catch no failure.
        IngestValidation.Validate(Payload(timestamp: Now.AddYears(-2)), Now)
            .Should().Be(IngestRejection.None);
    }

    [Fact]
    public void ModestClockSkewAheadIsAccepted()
    {
        // A phone an hour fast is not a broken clock, and refusing it would lose real events from
        // every device whose owner set the time by hand.
        IngestValidation.Validate(Payload(timestamp: Now.AddHours(1)), Now)
            .Should().Be(IngestRejection.None);
    }

    [Fact]
    public void TheSkewBoundaryIsWhereItSaysItIs()
    {
        IngestValidation.Validate(Payload(timestamp: Now.Add(IngestValidation.MaxClockSkewAhead)), Now)
            .Should().Be(IngestRejection.None);

        IngestValidation.Validate(
            Payload(timestamp: Now.Add(IngestValidation.MaxClockSkewAhead).AddSeconds(1)), Now)
            .Should().Be(IngestRejection.TimestampTooFarAhead);
    }

    [Fact]
    public void EveryRejectionHasItsOwnTag()
    {
        // The tag becomes a metric dimension. Two reasons sharing one tag would make a dashboard
        // report a cause that is not the cause.
        IngestRejection[] all = Enum.GetValues<IngestRejection>();
        string[] tags = all.Select(IngestValidation.Tag).ToArray();

        tags.Should().OnlyHaveUniqueItems();
        tags.Should().NotContain(string.Empty);
    }

    [Fact]
    public void TheLegacyPredicateStillAgreesWithTheDetailedOne()
    {
        // IsValidPayload is what the older call sites use. If the two ever disagreed, an event
        // could pass one path and be refused by the other for no reason a reader could find.
        IngestValidation.IsValidPayload(Payload()).Should().BeTrue();
        IngestValidation.IsValidPayload(Payload(projectId: "")).Should().BeFalse();
        IngestValidation.IsValidPayload(null).Should().BeFalse();
    }
}

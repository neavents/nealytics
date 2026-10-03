using FluentAssertions;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// Keeping one event type for less time than the rest.
///
/// Every analytics deployment has a highest-volume event type whose long-term value is nil once it
/// has been rolled up — impressions, usually, an order of magnitude above everything else.
///
/// <b>The measured semantics, which are not what you would guess.</b> On ClickHouse 26.7.1 a
/// compound TTL evaluates each clause independently and any match deletes. Two consequences, both
/// verified against a live server rather than reasoned about:
///
/// <list type="number">
/// <item>Clause order is irrelevant. A 45-day-old impression was removed by a 30 day rule written
/// <i>after</i> the 90 day catch-all.</item>
/// <item>The shortest applicable rule wins, so a per-type retention <i>longer</i> than the base does
/// nothing whatsoever. A 100-day-old row with a 365 day rule was deleted anyway by a 30 day base.
/// The configuration, the review and the log would all have said a year.</item>
/// </list>
///
/// The second is why the boot refuses that combination instead of applying it. It is a
/// recurring failure — a change that reports success while quietly keeping the old behaviour — and
/// here it destroys data.
/// </summary>
public class EventTypeRetentionTests
{
    private static EventTypeRetentionOptions Rule(string eventType, int days) =>
        new() { EventType = eventType, Days = days };

    /// <summary>
    /// The exact text ClickHouse 26.7.1 returns for a table created with
    /// <c>TTL ... INTERVAL 30 DAY DELETE WHERE event_type = 'item_impression', ... INTERVAL 7 DAY
    /// DELETE WHERE event_type = 'debug_ping', ... INTERVAL 90 DAY DELETE</c>.
    ///
    /// Copied from a live SHOW CREATE TABLE, not written from the documentation: INTERVAL becomes
    /// toIntervalDay, DELETE disappears because it is the default, and the condition survives
    /// verbatim. A parser written against the input form would never match the stored form, and the
    /// reconciler would then rewrite the TTL on every single boot.
    /// </summary>
    private const string LiveCompoundCreate = """
        CREATE TABLE nealytics_core.global_events
        (
            `event_type` LowCardinality(String),
            `timestamp` DateTime64(3, 'UTC')
        )
        ENGINE = ReplacingMergeTree
        PARTITION BY toYYYYMM(timestamp)
        ORDER BY (project_id, tenant_id, event_type, timestamp, event_id)
        TTL toDateTime(timestamp) + toIntervalDay(30) WHERE event_type = 'item_impression', toDateTime(timestamp) + toIntervalDay(7) WHERE event_type = 'debug_ping', toDateTime(timestamp) + toIntervalDay(90)
        SETTINGS index_granularity = 8192
        """;

    private const string LiveSimpleCreate = """
        CREATE TABLE nealytics_core.global_events
        (
            `event_type` LowCardinality(String)
        )
        ENGINE = ReplacingMergeTree
        ORDER BY (project_id, tenant_id, event_type, timestamp, event_id)
        TTL toDateTime(timestamp) + toIntervalDay(90)
        SETTINGS index_granularity = 8192
        """;

    [Fact]
    public void TheStoredFormOfACompoundTtlIsParsedBack()
    {
        List<(int Days, string? EventType)>? rules =
            ClickHouseSchemaMigrator.ReadRetentionRules(LiveCompoundCreate);

        rules.Should().BeEquivalentTo(new[]
        {
            (30, (string?)"item_impression"),
            (7, (string?)"debug_ping"),
            (90, (string?)null),
        });
    }

    [Fact]
    public void ASimpleTtlStillParses()
    {
        // A deployment created before this feature existed must not read as "no TTL" and get its
        // retention rewritten on the next boot for no reason.
        ClickHouseSchemaMigrator.ReadRetentionRules(LiveSimpleCreate)
            .Should().BeEquivalentTo(new[] { (90, (string?)null) });
    }

    [Fact]
    public void ATableWithNoTtlReadsAsUnknownRatherThanAsEmpty()
    {
        // Distinguishable from "no rules", because the reconciler leaves an unreadable TTL alone
        // rather than imposing one on a table someone deliberately created without.
        ClickHouseSchemaMigrator.ReadRetentionRules(
            "CREATE TABLE x (a Int) ENGINE = MergeTree ORDER BY a\nSETTINGS index_granularity = 8192")
            .Should().BeNull();
    }

    [Fact]
    public void AMatchingConfigurationIsNotDrift()
    {
        List<(int Days, string? EventType)> actual =
            ClickHouseSchemaMigrator.ReadRetentionRules(LiveCompoundCreate)!;

        ClickHouseSchemaMigrator.RetentionAgrees(
            actual, 90, [Rule("item_impression", 30), Rule("debug_ping", 7)])
            .Should().BeTrue();
    }

    [Fact]
    public void OrderIsNotDrift()
    {
        List<(int Days, string? EventType)> actual =
            ClickHouseSchemaMigrator.ReadRetentionRules(LiveCompoundCreate)!;

        // Order carries no meaning to ClickHouse, so reordering the config must not look like a
        // change. A reconciler that logs drift on every boot teaches people to stop reading the log.
        ClickHouseSchemaMigrator.RetentionAgrees(
            actual, 90, [Rule("debug_ping", 7), Rule("item_impression", 30)])
            .Should().BeTrue();
    }

    [Fact]
    public void ARemovedRuleIsDrift()
    {
        List<(int Days, string? EventType)> actual =
            ClickHouseSchemaMigrator.ReadRetentionRules(LiveCompoundCreate)!;

        ClickHouseSchemaMigrator.RetentionAgrees(actual, 90, [Rule("item_impression", 30)])
            .Should().BeFalse();
    }

    [Fact]
    public void AChangedBaseIsDrift()
    {
        List<(int Days, string? EventType)> actual =
            ClickHouseSchemaMigrator.ReadRetentionRules(LiveSimpleCreate)!;

        ClickHouseSchemaMigrator.RetentionAgrees(actual, 30, []).Should().BeFalse();
    }

    [Fact]
    public void TheGeneratedDdlRoundTripsThroughTheParser()
    {
        // The strongest cheap check available without a live server: what the migrator writes must
        // be what the migrator can read. It does not prove ClickHouse accepts it — the constants
        // above are the evidence for that — but it does prove the two halves agree with each other.
        string ddl = ClickHouseSchemaMigrator.BuildRetentionDdl(
            90, [Rule("item_impression", 30), Rule("debug_ping", 7)]);

        ddl.Should().Contain("MODIFY TTL");
        ddl.Should().Contain("materialize_ttl_after_modify = 0");

        ClickHouseSchemaMigrator.ReadRetentionRules("x\nTTL " + ddl[(ddl.IndexOf("MODIFY TTL", StringComparison.Ordinal) + 11)..])
            .Should().BeEquivalentTo(new[]
            {
                (30, (string?)"item_impression"),
                (7, (string?)"debug_ping"),
                (90, (string?)null),
            });
    }

    [Fact]
    public void NoRulesProducesTheSimpleTtlItAlwaysDid()
    {
        ClickHouseSchemaMigrator.BuildRetentionDdl(90)
            .Should().Be(ClickHouseSchemaMigrator.BuildRetentionDdl(90, []));
    }

    [Fact]
    public void ARetentionLongerThanTheBaseRefusesTheBoot()
    {
        // The finding this whole guard exists for. Measured: a 365 day rule did not save a
        // 100-day-old row from a 30 day base. Accepting this config would delete data on the base
        // schedule while every human-readable artifact claimed a year.
        Action act = () => ClickHouseSchemaMigrator.ValidateEventTypeRetention(90, [Rule("audit", 365)]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*audit*365*90*shortest*");
    }

    [Fact]
    public void ARetentionEqualToTheBaseRefusesTheBoot()
    {
        // Equal is not harmful, but it is a line of configuration that does nothing, and a config
        // language where a no-op is legal is one where nobody can tell a no-op from an effect.
        Action act = () => ClickHouseSchemaMigrator.ValidateEventTypeRetention(90, [Rule("audit", 90)]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AShorterRetentionIsAccepted()
    {
        Action act = () => ClickHouseSchemaMigrator.ValidateEventTypeRetention(
            90, [Rule("item_impression", 30)]);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("item_impression")]
    [InlineData("Item.View")]
    [InlineData("page:render")]
    [InlineData("web-vital")]
    public void AnOrdinaryEventTypeIsAccepted(string eventType)
    {
        // Deliberately wider than the dimension pattern: an event vocabulary belongs to the
        // deployment, and a clone may well use camelCase or dots.
        Action act = () => ClickHouseSchemaMigrator.ValidateEventTypeRetention(90, [Rule(eventType, 30)]);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("x' OR '1'='1")]
    [InlineData("a'; DROP TABLE global_events; --")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("9leading_digit")]
    [InlineData("back\\slash")]
    public void AnUnsafeEventTypeRefusesTheBoot(string eventType)
    {
        // This name is interpolated into DDL. There is no registry of event types to check against
        // — ingestion accepts whatever it is sent — so the shape is the entire defence.
        Action act = () => ClickHouseSchemaMigrator.ValidateEventTypeRetention(90, [Rule(eventType, 30)]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ADuplicateEventTypeRefusesTheBoot()
    {
        Action act = () => ClickHouseSchemaMigrator.ValidateEventTypeRetention(
            90, [Rule("item_impression", 30), Rule("item_impression", 14)]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*more than once*");
    }

    [Fact]
    public void ZeroDaysRefusesTheBoot()
    {
        // "Keep it for no time" is not a retention policy, it is a request to stop collecting, and
        // the honest way to do that is to stop sending the event.
        Action act = () => ClickHouseSchemaMigrator.ValidateEventTypeRetention(90, [Rule("noise", 0)]);

        act.Should().Throw<InvalidOperationException>();
    }
}

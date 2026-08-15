using FluentAssertions;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

public class RetentionReconciliationTests
{
    [Theory]
    [InlineData("... TTL toDateTime(timestamp) + toIntervalDay(90) SETTINGS ...", "90")]
    [InlineData("... TTL toDateTime(timestamp) + toIntervalDay(30)", "30")]
    [InlineData("... TTL toDateTime(timestamp) + INTERVAL 400 DAY DELETE", "400")]
    [InlineData("... ttl todatetime(timestamp) + interval 7 day delete", "7")]
    public void ReadRetentionDays_ParsesBothRenderings(string createQuery, string expected)
    {
        ClickHouseSchemaMigrator.ReadRetentionDays(createQuery).Should().Be(
            expected,
            "ClickHouse renders the TTL as toIntervalDay(n) in create_table_query while the init "
            + "script writes INTERVAL n DAY, and reading only one form makes every boot think the "
            + "retention drifted");
    }

    [Theory]
    [InlineData("CREATE TABLE x (a UInt8) ENGINE = MergeTree ORDER BY a")]
    [InlineData("")]
    [InlineData(null)]
    public void ReadRetentionDays_ReturnsNullWhenThereIsNoTtl(string? createQuery)
    {
        ClickHouseSchemaMigrator.ReadRetentionDays(createQuery).Should().BeNull(
            "an unreadable TTL must leave the table alone rather than guess at one");
    }

    [Fact]
    public void ReadRetentionDays_IgnoresATtlOnADifferentColumn()
    {
        ClickHouseSchemaMigrator.ReadRetentionDays(
            "... TTL toDateTime(received_at) + toIntervalDay(30)").Should().BeNull();
    }

    [Fact]
    public void BuildRetentionDdl_DoesNotMaterialiseExistingParts()
    {
        string ddl = ClickHouseSchemaMigrator.BuildRetentionDdl(45);

        ddl.Should().Contain("MODIFY TTL toDateTime(timestamp) + INTERVAL 45 DAY DELETE");
        ddl.Should().Contain("materialize_ttl_after_modify = 0",
            "rewriting every existing part on boot would turn a config change into hours of merge "
            + "load on a table this size");
    }
}

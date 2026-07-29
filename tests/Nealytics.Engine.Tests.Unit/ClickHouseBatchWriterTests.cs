using FluentAssertions;
using Nealytics.Engine.Features.BatchProcessor;

namespace Nealytics.Engine.Tests.Unit;

public class ClickHouseBatchWriterTests
{
    [Fact]
    public void BuildInsertCommand_WithAsyncInsert_AppendsWaitForAsyncSettings()
    {
        string command = ClickHouseBatchWriter.BuildInsertCommand(asyncInsert: true);

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
        string command = ClickHouseBatchWriter.BuildInsertCommand(asyncInsert: false);

        command.Should().NotContain("SETTINGS");
        command.Should().NotContain("async_insert");
        command.Should().EndWith("VALUES");
    }

    [Fact]
    public void BuildInsertCommand_KeepsColumnOrder_MatchingSchema()
    {
        string command = ClickHouseBatchWriter.BuildInsertCommand(asyncInsert: true);

        // menu_id, section_id and table_id were promoted out of metadata_json so aggregates can
        // group on them; device_class/os/browser/country are derived at the edge from the
        // User-Agent and Cloudflare request metadata.
        command.Should().Contain(
            "(event_id, project_id, tenant_id, session_id, user_id, event_type, item_id, "
            + "menu_id, section_id, table_id, device_class, os, browser, country, "
            + "metadata_json, timestamp)");
    }
}

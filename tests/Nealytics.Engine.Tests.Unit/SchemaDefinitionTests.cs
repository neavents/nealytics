using FluentAssertions;

namespace Nealytics.Engine.Tests.Unit;

public class SchemaDefinitionTests
{
    private static string ReadInitSql()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "clickhouse-init.sql");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("clickhouse-init.sql not found walking up from test output directory.");
    }

    [Fact]
    public void Schema_UsesReplacingMergeTree_ForIdempotentWalReplay()
    {
        string sql = ReadInitSql();
        sql.Should().Contain("ReplacingMergeTree",
            "duplicate events replayed from the WAL after a crash must collapse to a single row");
    }

    [Fact]
    public void Schema_IncludesEventIdInSortingKey_SoDuplicatesCollapse()
    {
        string sql = ReadInitSql();
        sql.Should().MatchRegex(@"ORDER BY \([^)]*\bevent_id\b[^)]*\)",
            "event_id must be part of the ORDER BY key for ReplacingMergeTree to deduplicate by event");
    }

    [Fact]
    public void Schema_DeclaresRetentionTtl()
    {
        string sql = ReadInitSql();
        sql.Should().Contain("TTL", "the events table must bound growth with a retention TTL");
        sql.Should().Contain("INTERVAL 90 DAY");
        sql.Should().Contain("ttl_only_drop_parts = 1",
            "retention should drop whole parts rather than run per-row deletes");
    }
}

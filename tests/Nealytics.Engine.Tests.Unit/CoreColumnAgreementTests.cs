using System.Text.RegularExpressions;
using FluentAssertions;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

public class CoreColumnAgreementTests
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

    private static IReadOnlyList<string> InitSqlColumns()
    {
        string sql = ReadInitSql();

        int createAt = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        createAt.Should().BeGreaterThanOrEqualTo(0, "clickhouse-init.sql must declare the events table");

        int open = sql.IndexOf('(', createAt);
        open.Should().BeGreaterThanOrEqualTo(0, "the column list must be parenthesised");

        int depth = 0;
        int close = -1;
        for (int i = open; i < sql.Length; i++)
        {
            if (sql[i] == '(')
            {
                depth++;
            }
            else if (sql[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    close = i;
                    break;
                }
            }
        }

        close.Should().BeGreaterThan(open, "the column list must be closed");

        // Comments come out before the split, not during it. A comma inside a `--` comment is not a
        // column separator, and treating it as one lands a segment boundary mid-sentence -- the
        // first prose word then parses as a column name, and this test fails claiming the schema is
        // missing a column called "so".
        string body = Regex.Replace(sql[(open + 1)..close], "--[^\n]*", string.Empty);
        List<string> columns = [];
        int segmentStart = 0;
        depth = 0;

        for (int i = 0; i <= body.Length; i++)
        {
            bool atEnd = i == body.Length;
            if (!atEnd && body[i] == '(')
            {
                depth++;
            }
            else if (!atEnd && body[i] == ')')
            {
                depth--;
            }

            if (atEnd || (body[i] == ',' && depth == 0))
            {
                string segment = body[segmentStart..i];
                segmentStart = i + 1;

                foreach (string line in segment.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Match match = Regex.Match(trimmed, @"^([a-z][a-z0-9_]*)\s");
                    if (match.Success)
                    {
                        columns.Add(match.Groups[1].Value);
                    }
                    break;
                }
            }
        }

        columns.Should().NotBeEmpty("the parser must find the declared columns");
        return columns;
    }

    private static IReadOnlyList<string> CoreStatementColumns() =>
        [.. ClickHouseSchemaMigrator.CoreStatements
            .Select(statement => Regex.Match(statement, @"ADD COLUMN IF NOT EXISTS\s+([a-z][a-z0-9_]*)"))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)];

    [Fact]
    public void InsertColumns_MatchTheDeclaredTable()
    {
        TelemetryColumnLayout.CoreColumns.Should().BeEquivalentTo(
            InitSqlColumns(),
            "the INSERT names every core column verbatim — one the table does not have fails the "
            + "whole batch, not one row, and telemetry has no retry");
    }

    [Fact]
    public void ReservedColumns_CoverEveryColumnTheEngineWrites()
    {
        DimensionRegistry.ReservedColumns.Should().BeEquivalentTo(
            TelemetryColumnLayout.CoreColumns,
            "a dimension allowed to shadow a core column generates DDL that either fails obscurely "
            + "or writes two sources into one column");
    }

    [Fact]
    public void GroupableColumns_AreAllRealColumns()
    {
        BreakdownColumns columns = new(new DimensionRegistry(new TelemetryEngineOptions()));

        columns.Allowed.Should().BeSubsetOf(
            TelemetryColumnLayout.CoreColumns,
            "the breakdown allowlist is the injection boundary — a name in it that is not a column "
            + "turns a 400 into a 500 from ClickHouse");
    }

    [Fact]
    public void ReconciledColumns_ExistInTheDeclaredTable()
    {
        CoreStatementColumns().Should().BeSubsetOf(
            InitSqlColumns(),
            "clickhouse-init.sql runs once on an empty volume and the reconciler catches up every "
            + "existing deployment, so the two must describe the same table");
    }

    [Fact]
    public void ReconciledColumns_CoverEveryColumnAddedAfterTheOriginalSchema()
    {
        CoreStatementColumns().Should().Contain(
            ["user_id", "object_id", "device_class", "os", "browser", "country"],
            "clickhouse-init.sql only runs on an empty data volume, so a column added after a "
            + "deployment was created reaches it through the reconciler or not at all");
    }
}

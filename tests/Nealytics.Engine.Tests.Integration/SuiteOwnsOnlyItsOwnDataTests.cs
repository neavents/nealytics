using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Integration;

/// <summary>
/// The test suite may not destroy data it did not create.
///
/// <para><b>Why this is a test.</b> There is no separate test database. The connection string
/// defaults to <c>127.0.0.1:9000/nealytics_core</c> and <c>docker-compose.yml</c> points the engine
/// at the same database, so on any machine running the estate, "the table the tests use" and "the
/// table the dashboard reads" are one table. Two classes ran
/// <c>TRUNCATE TABLE nealytics_core.global_events</c> in their setup, so every <c>dotnet test</c>
/// silently wiped the owner's analytics and left the table holding only the suite's own fixtures.
///
/// <para>It is invisible from inside the suite: every test still passes — passes <i>better</i>, on a
/// clean table — and the damage only shows up as a dashboard that reports "not measured" hours
/// later, with nothing connecting the two. A green run is exactly what it looked like.</para>
/// </summary>
[Collection("ClickHouse")]
public class SuiteOwnsOnlyItsOwnDataTests
{
    /// <summary>
    /// A project id no test writes under, standing in for real traffic.
    /// </summary>
    private const string ForeignProject = "not-a-test-project-do-not-delete";

    [Fact]
    public async Task Cleanup_removes_the_named_projects_and_leaves_everything_else()
    {
        await SeedAsync(ForeignProject);
        await SeedAsync("p-cleanup-probe");

        (await ClickHouseTestSupport.CountAsync(ForeignProject)).Should().BeGreaterThan(0,
            "the sentinel must exist before the cleanup, or this test proves nothing");
        (await ClickHouseTestSupport.CountAsync("p-cleanup-probe")).Should().BeGreaterThan(0);

        await ClickHouseTestSupport.DeleteProjectsAsync("p-cleanup-probe");

        (await ClickHouseTestSupport.CountAsync("p-cleanup-probe")).Should().Be(0,
            "the project it was told to delete should be gone");
        (await ClickHouseTestSupport.CountAsync(ForeignProject)).Should().BeGreaterThan(0,
            "a project it was NOT told to delete must survive — this is the whole point");

        // Leave nothing behind: the sentinel is this test's own litter, not real traffic.
        await ClickHouseTestSupport.DeleteProjectsAsync(ForeignProject);
    }

    [Fact]
    public async Task Cleanup_with_no_projects_deletes_nothing()
    {
        // `IN ()` is a syntax error, so an empty list must short-circuit rather than reach the
        // server — and it must certainly not degrade into "delete everything".
        await SeedAsync(ForeignProject);

        await ClickHouseTestSupport.DeleteProjectsAsync();

        (await ClickHouseTestSupport.CountAsync(ForeignProject)).Should().BeGreaterThan(0);

        await ClickHouseTestSupport.DeleteProjectsAsync(ForeignProject);
    }

    /// <summary>
    /// No test may issue a statement that clears the shared table wholesale.
    ///
    /// <remarks>
    /// A source scan rather than a behavioural assertion, deliberately. The behaviour under test is
    /// "nothing in this suite wipes the table", and there is no point during an xUnit run at which
    /// that can be observed — a class that truncates in its own setup does so while this test is
    /// not looking, and whether the damage is visible here depends on collection scheduling. The
    /// text of the suite is the only thing that can be checked deterministically.
    /// </remarks>
    /// </summary>
    [Fact]
    public void No_test_truncates_or_drops_the_shared_table()
    {
        string directory = Path.GetDirectoryName(ThisFile())!;
        string[] sources = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();

        sources.Length.Should().BeGreaterThan(5,
            "if the scan found no files it would pass for the wrong reason");

        // Matches TRUNCATE/DROP of the events table however it is spelled or spaced. `DELETE WHERE`
        // is not matched: a scoped delete is the supported way to clean up. The one drop a test may
        // own is of a rollup table it created under its own name, which lives beside the shared
        // table and not in it.
        Regex destructive = new(
            @"\b(TRUNCATE\s+TABLE|DROP\s+TABLE(?!\s+IF\s+EXISTS\s+nealytics_core\.rollup_))\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        List<string> offenders = [];

        foreach (string path in sources)
        {
            string text = File.ReadAllText(path);

            // This file names the statement in its own prose to explain the defect.
            if (Path.GetFileName(path) == Path.GetFileName(ThisFile())) continue;

            foreach (Match match in destructive.Matches(WithoutInjectionPayloads(WithoutComments(text))))
            {
                offenders.Add($"{Path.GetFileName(path)}: {match.Value}");
            }
        }

        offenders.Should().BeEmpty(
            "the suite shares nealytics_core.global_events with the running estate; clean up with "
            + "ClickHouseTestSupport.DeleteProjectsAsync and name the projects you own");
    }

    /// <summary>
    /// Comments are stripped before scanning, so prose explaining the ban does not trip it.
    /// </summary>
    private static string WithoutComments(string source) =>
        Regex.Replace(
            Regex.Replace(source, @"/\*[\s\S]*?\*/", " "),
            @"//[^\n]*", " ");

    /// <summary>
    /// Strips <c>Uri.EscapeDataString(...)</c> arguments before scanning.
    ///
    /// <remarks>
    /// A SQL-injection test has to contain the statement it is trying to smuggle —
    /// <c>groupBy=event_type; DROP TABLE nealytics_core.global_events</c> is the payload, and the
    /// assertion is precisely that it does NOT execute. Caught unmodified on the first run of this
    /// guard, in <c>BreakdownIntegrationTests</c>. Percent-encoding is the one place such a string
    /// can appear while provably travelling to the server as a URL parameter rather than as a
    /// statement, so it is the one honest exclusion. Anything destructive written outside it trips
    /// the guard, which is the safe direction.
    /// </remarks>
    /// </summary>
    private static string WithoutInjectionPayloads(string source) =>
        Regex.Replace(source, @"Uri\.EscapeDataString\s*\([\s\S]*?\)", " ");

    private static string ThisFile([CallerFilePath] string path = "") => path;

    /// <summary>
    /// Writes one row straight to ClickHouse. Deliberately not through the ingest endpoint: this
    /// test is about the cleanup helper, and routing through the batch writer would make a failure
    /// here ambiguous between the two.
    /// </summary>
    private static async Task SeedAsync(string projectId)
    {
        await using ClickHouseConnection connection = new ClickHouseConnection(ClickHouseTestSupport.ConnectionString);
        await connection.OpenAsync();

        // The column-writer path, the same one ClickHouseBatchWriter uses. An `INSERT ... VALUES`
        // with a bound parameter is not supported by this driver — it rewrites the parameter into a
        // table expression and the server answers "Unknown table expression identifier".
        await using ClickHouseColumnWriter writer = await connection.CreateColumnWriterAsync(
            "INSERT INTO nealytics_core.global_events "
            + "(event_id, project_id, tenant_id, session_id, event_type, timestamp) VALUES",
            CancellationToken.None);

        object[] columns =
        [
            new[] { Guid.NewGuid() },
            new[] { projectId },
            new[] { "sentinel-tenant" },
            new[] { "sentinel-session" },
            new[] { "sentinel" },
            new[] { DateTime.UtcNow },
        ];

        await writer.WriteTableAsync(columns, 1, CancellationToken.None);
        await writer.EndWriteAsync(CancellationToken.None);
    }
}

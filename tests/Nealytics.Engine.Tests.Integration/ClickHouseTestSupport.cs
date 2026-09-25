using System.Data;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Integration;

public static class ClickHouseTestSupport
{
    public static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("TelemetryEngine__ClickHouseConnectionString")
        ?? "Host=127.0.0.1;Port=9000;Database=nealytics_core;User=default;Password=;";

    public static string RecentRange
    {
        get
        {
            DateTime to = DateTime.UtcNow.Date.AddDays(2);
            return FormattableString.Invariant($"from={to.AddDays(-82):yyyy-MM-ddTHH:mm:ssZ}&to={to:yyyy-MM-ddTHH:mm:ssZ}");
        }
    }

    public static async Task<long> CountAsync(string projectId)
    {
        await using ClickHouseConnection connection = new ClickHouseConnection(ConnectionString);
        await connection.OpenAsync();
        await using ClickHouseCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count() FROM nealytics_core.global_events WHERE project_id = {p:String}";
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "p", Value = projectId });
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    public static async Task ExecuteAsync(string sql)
    {
        await using ClickHouseConnection connection = new ClickHouseConnection(ConnectionString);
        await connection.OpenAsync();
        await using ClickHouseCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Removes every row belonging to the named projects, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>This replaced <c>TRUNCATE TABLE nealytics_core.global_events</c>, which two test
    /// classes ran in <c>InitializeAsync</c>. There is no separate test database: the connection
    /// string defaults to <c>127.0.0.1:9000/nealytics_core</c>, which on a developer machine is the
    /// container the dashboard reads. So <c>dotnet test</c> destroyed the estate's analytics, every
    /// time, and left the table holding nothing but the suite's own <c>p-bd</c>/<c>p-flush</c>
    /// fixtures. Measured before this change: 55 rows, all synthetic, not one belonging to project
    /// <c>neavents</c>.</para>
    ///
    /// <para>Nothing needed the table empty — every assertion in those classes is scoped by a JWT
    /// carrying <c>project_id</c>, and each class uses project ids no other class touches. What
    /// they actually needed was their <i>own</i> rows gone, so a re-run does not count the previous
    /// run's events twice. That is what this does.</para>
    ///
    /// <para><c>mutations_sync = 2</c> because a ClickHouse mutation is asynchronous by default:
    /// the statement returns once the mutation is queued, and a test that read straight after would
    /// race it and see the rows it just asked to delete. 2 waits for all replicas.</para>
    ///
    /// <para><b>The ids are validated and inlined, not bound.</b> A bound parameter does not survive
    /// into a mutation: ClickHouse stores the statement and re-executes it in a background context
    /// that has neither the parameter values nor a default database, and the delete fails after the
    /// call has already returned success — observed as
    /// <c>Exception happened during execution of mutations ... Default database is not selected</c>.
    /// So the value has to be in the text. <see cref="ValidProjectId"/> is what makes that safe: an
    /// id that is not a plain identifier throws instead of reaching the statement.</para>
    /// </remarks>
    public static async Task DeleteProjectsAsync(params string[] projectIds)
    {
        ArgumentNullException.ThrowIfNull(projectIds);

        // An empty list would otherwise generate `IN ()`, which is a syntax error rather than the
        // no-op it reads as.
        if (projectIds.Length == 0) return;

        string[] literals = new string[projectIds.Length];
        for (int i = 0; i < projectIds.Length; i++)
        {
            string projectId = projectIds[i];

            if (!ValidProjectId.IsMatch(projectId))
            {
                throw new ArgumentException(
                    $"'{projectId}' is not a plain project id. This value is inlined into a DELETE "
                    + "against the shared events table, so anything that is not [A-Za-z0-9._-] is "
                    + "refused rather than escaped.",
                    nameof(projectIds));
            }

            literals[i] = $"'{projectId}'";
        }

        await using ClickHouseConnection connection = new ClickHouseConnection(ConnectionString);
        await connection.OpenAsync();
        await using ClickHouseCommand command = connection.CreateCommand();

        command.CommandText =
            "ALTER TABLE nealytics_core.global_events DELETE WHERE project_id IN ("
            + string.Join(", ", literals)
            + ") SETTINGS mutations_sync = 2";

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Project ids this helper will inline. Anything else is a bug, not an escaping job.</summary>
    private static readonly System.Text.RegularExpressions.Regex ValidProjectId =
        // 256, not 128: EdgeCaseTests deliberately writes under a 200-character project id to prove
        // long fields are accepted, and that row has to be cleanable like any other.
        new("^[A-Za-z0-9._-]{1,256}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static Task OptimizeFinalAsync() =>
        ExecuteAsync("OPTIMIZE TABLE nealytics_core.global_events FINAL");
}

namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Configuration;
using Octonica.ClickHouseClient;

/// <summary>
/// Reconciles the declared dimensions against the live shape of <c>global_events</c>.
///
/// <c>clickhouse-init.sql</c> is mounted into <c>docker-entrypoint-initdb.d</c>, so it runs
/// exactly once, against an empty data volume. Any deployment that already has data never sees
/// it — which is why this exists at all. It also cannot know the deployment's dimensions, which
/// are declared in configuration; those columns are created here or nowhere.
///
/// It runs before the batch writer takes traffic: a missing column rejects <b>entire batches</b>,
/// not one row, and telemetry has no retry once the beacon returns.
///
/// <para><b>On concurrency.</b> There is deliberately no advisory lock. ClickHouse has none, and
/// the Postgres lesson does not transfer: <c>ADD COLUMN IF NOT EXISTS</c> is idempotent so two
/// replicas racing converge, and replicas of one deployment read the same config so they cannot
/// disagree about a type. A genuine disagreement means two different configs deployed at once —
/// a deploy error, which the type-mismatch check below turns into a refused boot.</para>
///
/// <para><b>Two kinds of failure, two answers.</b> A <see cref="SchemaReconciliationException"/> —
/// a declared type that contradicts the column, or a column holding data nobody declared — is a
/// disagreement between config and storage. Restarting will not fix it, so the boot is refused.
/// Failing to <i>reach</i> ClickHouse is the opposite: transient, and refusing to start would take
/// analytics reads down because the write path could not be widened. That is logged loudly and the
/// service comes up; the batch writer's own retries handle the rest.</para>
/// </summary>
public sealed partial class ClickHouseSchemaMigrator : IHostedService
{
    private const string Database = "nealytics_core";
    private const string Table = "global_events";

    /// <summary>
    /// Raised when configuration and storage disagree about the schema. Distinct from any
    /// connection failure on purpose: this one is never swallowed.
    /// </summary>
    public sealed class SchemaReconciliationException : InvalidOperationException
    {
        public SchemaReconciliationException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Core columns, kept in step with <c>clickhouse-init.sql</c> by hand — the two describe the
    /// same table. Nothing deployment-specific belongs here; that is what the registry is for.
    /// </summary>
    private static readonly IReadOnlyList<string> CoreStatements =
    [
        $"ALTER TABLE {Database}.{Table} ADD COLUMN IF NOT EXISTS device_class LowCardinality(String) DEFAULT ''",
        $"ALTER TABLE {Database}.{Table} ADD COLUMN IF NOT EXISTS os LowCardinality(String) DEFAULT ''",
        $"ALTER TABLE {Database}.{Table} ADD COLUMN IF NOT EXISTS browser LowCardinality(String) DEFAULT ''",
        $"ALTER TABLE {Database}.{Table} ADD COLUMN IF NOT EXISTS country LowCardinality(String) DEFAULT ''",
    ];

    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly DimensionRegistry _registry;
    private readonly ILogger<ClickHouseSchemaMigrator> _logger;

    public ClickHouseSchemaMigrator(
        ClickHouseConnectionFactory connectionFactory,
        DimensionRegistry registry,
        ILogger<ClickHouseSchemaMigrator> logger)
    {
        _connectionFactory = connectionFactory;
        _registry = registry;
        _logger = logger;
    }

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information,
        Message = "Dimension '{Dimension}' is declared but missing from {Database}.{Table}. "
            + "Adding it as {ClickHouseType}.")]
    private static partial void LogAddingDimension(
        ILogger logger, string dimension, string database, string table, string clickHouseType);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Information,
        Message = "ClickHouse schema reconciled — {CoreCount} core statements applied, "
            + "{DimensionCount} declared dimension(s) present, {AddedCount} added.")]
    private static partial void LogReconciled(
        ILogger logger, int coreCount, int dimensionCount, int addedCount);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Critical,
        Message = "SCHEMA REFUSED: dimension '{Dimension}' is declared as {DeclaredType} but the "
            + "column in {Database}.{Table} is {ActualType}. Refusing to start rather than coerce "
            + "it — a coerced column either drops every existing value or starts writing values "
            + "the old rows cannot be compared against, and that is not recoverable. Fix the "
            + "declared type or rename the dimension.")]
    private static partial void LogTypeMismatch(
        ILogger logger, string dimension, string declaredType, string database, string table, string actualType);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Critical,
        Message = "SCHEMA REFUSED: column '{Column}' in {Database}.{Table} holds {RowCount} "
            + "non-null row(s) but is not declared under TelemetryEngine:Dimensions. Refusing to "
            + "start. If you meant to stop collecting it, declare it with Retired: true — that "
            + "keeps the column and its rows. Deletion must not be expressible by absence, because "
            + "absence is what a mistake looks like.")]
    private static partial void LogUndeclaredColumnWithData(
        ILogger logger, string column, string database, string table, ulong rowCount);

    [LoggerMessage(EventId = 8005, Level = LogLevel.Information,
        Message = "Dimension '{Dimension}' is retired: its column and {RowCount} row(s) are kept, "
            + "ingestion refuses the name, and the query API does not offer it.")]
    private static partial void LogRetiredDimension(ILogger logger, string dimension, ulong rowCount);

    [LoggerMessage(EventId = 8006, Level = LogLevel.Warning,
        Message = "Column '{Column}' in {Database}.{Table} is not declared and holds no data. "
            + "Leaving it alone — this service never drops a column.")]
    private static partial void LogUndeclaredEmptyColumn(
        ILogger logger, string column, string database, string table);

    [LoggerMessage(EventId = 8007, Level = LogLevel.Error,
        Message = "ClickHouse schema reconciliation could not run — the database was unreachable. "
            + "Declared dimensions have NOT been verified and any missing column has NOT been "
            + "created; ingest will reject batches naming one until this succeeds. Query endpoints "
            + "stay up deliberately: a database blip must not take analytics reads down with it.")]
    private static partial void LogReconciliationUnreachable(ILogger logger, Exception exception);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SchemaReconciliationException)
        {
            // Config and storage disagree. Deterministic, and a restart will not change it — the
            // whole point of the guard is that the boot stops here.
            throw;
        }
        catch (Exception ex)
        {
            LogReconciliationUnreachable(_logger, ex);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using PooledClickHouseConnection pooled =
            await _connectionFactory.AcquireAsync(cancellationToken).ConfigureAwait(false);

        foreach (string statement in CoreStatements)
        {
            await ExecuteAsync(pooled, statement, cancellationToken).ConfigureAwait(false);
        }

        Dictionary<string, string> live =
            await ReadLiveColumnsAsync(pooled, cancellationToken).ConfigureAwait(false);

        int added = 0;

        foreach (Dimension dimension in _registry.Declared)
        {
            if (!live.TryGetValue(dimension.Name, out string? actualType))
            {
                if (dimension.Retired)
                {
                    // Retired and never created. Nothing to keep, nothing to add.
                    LogRetiredDimension(_logger, dimension.Name, 0);
                    continue;
                }

                LogAddingDimension(_logger, dimension.Name, Database, Table, dimension.ClickHouseType);

                string ddl =
                    $"ALTER TABLE {Database}.{Table} ADD COLUMN IF NOT EXISTS "
                    + $"{dimension.Name} {dimension.ClickHouseType}"
                    + (dimension.DefaultExpression is null ? string.Empty : $" DEFAULT {dimension.DefaultExpression}");

                await ExecuteAsync(pooled, ddl, cancellationToken).ConfigureAwait(false);
                added++;
                continue;
            }

            if (!TypesMatch(dimension.ClickHouseType, actualType))
            {
                LogTypeMismatch(
                    _logger, dimension.Name, dimension.ClickHouseType, Database, Table, actualType);

                throw new SchemaReconciliationException(
                    $"Dimension '{dimension.Name}' is declared as {dimension.ClickHouseType} but the "
                    + $"column in {Database}.{Table} is {actualType}.");
            }

            if (dimension.Retired)
            {
                ulong retiredRows =
                    await CountNonNullAsync(pooled, dimension.Name, cancellationToken).ConfigureAwait(false);
                LogRetiredDimension(_logger, dimension.Name, retiredRows);
            }
        }

        await RefuseUndeclaredColumnsWithDataAsync(pooled, live, cancellationToken).ConfigureAwait(false);

        LogReconciled(_logger, CoreStatements.Count, _registry.Declared.Count, added);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Whitespace is not semantic in a ClickHouse type, so it is normalised away before comparing.
    /// The raw strings are what gets logged, so a refusal still shows exactly what was read.
    /// </summary>
    internal static bool TypesMatch(string declared, string actual) =>
        string.Equals(Normalize(declared), Normalize(actual), StringComparison.Ordinal);

    private static string Normalize(string type) => type.Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// The most important rule here.
    ///
    /// Without it, deleting one line from config leaves the column and its data in place while new
    /// writes for that dimension start being rejected and the query API stops offering it. A hole
    /// begins accumulating and the widget that would have shown you goes blank at the same instant.
    /// The symptom and the evidence disappear together, and someone notices three months later.
    /// </summary>
    private async Task RefuseUndeclaredColumnsWithDataAsync(
        PooledClickHouseConnection pooled,
        Dictionary<string, string> live,
        CancellationToken cancellationToken)
    {
        List<string> offenders = [];

        foreach (string column in live.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            if (DimensionRegistry.ReservedColumns.Contains(column) || _registry.IsDeclared(column))
            {
                continue;
            }

            ulong rowCount = await CountNonNullAsync(pooled, column, cancellationToken).ConfigureAwait(false);

            if (rowCount == 0)
            {
                LogUndeclaredEmptyColumn(_logger, column, Database, Table);
                continue;
            }

            LogUndeclaredColumnWithData(_logger, column, Database, Table, rowCount);
            offenders.Add($"{column} ({rowCount} row(s))");
        }

        if (offenders.Count > 0)
        {
            throw new SchemaReconciliationException(
                $"Undeclared column(s) in {Database}.{Table} still holding data: "
                + $"{string.Join(", ", offenders)}. Declare them under TelemetryEngine:Dimensions, "
                + "with Retired: true if collection should stop.");
        }
    }

    private async Task<Dictionary<string, string>> ReadLiveColumnsAsync(
        PooledClickHouseConnection pooled, CancellationToken cancellationToken)
    {
        Dictionary<string, string> live = new(StringComparer.Ordinal);

        await using ClickHouseCommand command = pooled.Connection.CreateCommand(
            "SELECT name, type FROM system.columns "
            + "WHERE database = {database:String} AND table = {table:String}");

        command.Parameters.Add(new ClickHouseParameter { ParameterName = "database", Value = Database });
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "table", Value = Table });

        await using DbDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            live[reader.GetString(0)] = reader.GetString(1);
        }

        return live;
    }

    /// <summary>
    /// Counts rows where the column has a value.
    ///
    /// The column name is interpolated because it comes from <c>system.columns</c> — the database's
    /// own catalogue, never a caller — and ClickHouse has no parameter form for an identifier. It
    /// is quoted with backticks so a name needing quoting still parses.
    /// </summary>
    private static async Task<ulong> CountNonNullAsync(
        PooledClickHouseConnection pooled, string column, CancellationToken cancellationToken)
    {
        await using ClickHouseCommand command = pooled.Connection.CreateCommand(
            $"SELECT count() FROM {Database}.{Table} WHERE `{column.Replace("`", "``", StringComparison.Ordinal)}` "
            + "IS NOT NULL AND toString(`"
            + column.Replace("`", "``", StringComparison.Ordinal)
            + "`) != ''");

        object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is null ? 0UL : Convert.ToUInt64(scalar);
    }

    private static async Task ExecuteAsync(
        PooledClickHouseConnection pooled, string statement, CancellationToken cancellationToken)
    {
        await using ClickHouseCommand command = pooled.Connection.CreateCommand(statement);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Configuration;
using Octonica.ClickHouseClient;
using Octonica.ClickHouseClient.Exceptions;

public sealed partial class ClickHouseSchemaMigrator : IHostedService
{
    private const string Database = "nealytics_core";
    private const string Table = "global_events";

    public sealed class SchemaReconciliationException : InvalidOperationException
    {
        public SchemaReconciliationException(string message) : base(message)
        {
        }
    }

    internal static readonly IReadOnlyList<string> CoreStatements = CoreStatementsFor(string.Empty);

    internal static IReadOnlyList<string> CoreStatementsFor(string onCluster) =>
    [
        $"ALTER TABLE {Database}.{Table}{onCluster} ADD COLUMN IF NOT EXISTS user_id Nullable(String)",
        $"ALTER TABLE {Database}.{Table}{onCluster} ADD COLUMN IF NOT EXISTS object_id Nullable(String)",
        $"ALTER TABLE {Database}.{Table}{onCluster} ADD COLUMN IF NOT EXISTS device_class LowCardinality(String) DEFAULT ''",
        $"ALTER TABLE {Database}.{Table}{onCluster} ADD COLUMN IF NOT EXISTS os LowCardinality(String) DEFAULT ''",
        $"ALTER TABLE {Database}.{Table}{onCluster} ADD COLUMN IF NOT EXISTS browser LowCardinality(String) DEFAULT ''",
        $"ALTER TABLE {Database}.{Table}{onCluster} ADD COLUMN IF NOT EXISTS country LowCardinality(String) DEFAULT ''",
        $"ALTER TABLE {Database}.{Table}{onCluster} ADD COLUMN IF NOT EXISTS seq UInt32 DEFAULT 0",
        $"ALTER TABLE {Database}.{Table}{onCluster} ADD COLUMN IF NOT EXISTS traffic_class LowCardinality(String) DEFAULT 'normal'",
        $"ALTER TABLE {Database}.{Table}{onCluster} ADD COLUMN IF NOT EXISTS page_path String DEFAULT ''",
        $"ALTER TABLE {Database}.{Table}{onCluster} ADD COLUMN IF NOT EXISTS referrer LowCardinality(String) DEFAULT ''",
        $"ALTER TABLE {Database}.{Table}{onCluster} ADD COLUMN IF NOT EXISTS ingested_at DateTime64(3, 'UTC') DEFAULT now64(3)",

        $"ALTER TABLE {Database}.{Table}{onCluster} ADD INDEX IF NOT EXISTS idx_object_id object_id TYPE bloom_filter(0.01) GRANULARITY 4",
    ];

    private static readonly Regex RetentionPattern = new(
        @"TTL\s+toDateTime\(timestamp\)\s*\+\s*(?:toInterval[Dd]ay\((\d+)\)|INTERVAL\s+(\d+)\s+DAY)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RetentionRulePattern = new(
        @"toDateTime\(timestamp\)\s*\+\s*(?:toInterval[Dd]ay\((?<d1>\d+)\)|INTERVAL\s+(?<d2>\d+)\s+DAY)"
        + @"(?:\s+DELETE)?(?:\s+WHERE\s+event_type\s*=\s*'(?<event>[^']*)')?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EventTypeNamePattern =
        new(@"^[A-Za-z][A-Za-z0-9_.:-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly DimensionRegistry _registry;
    private readonly MeasureRegistry _measures;
    private readonly RollupRegistry _rollups;
    private readonly bool _backfillRollups;
    private readonly int _retentionDays;
    private readonly List<EventTypeRetentionOptions> _eventTypeRetention;
    private readonly ILogger<ClickHouseSchemaMigrator> _logger;
    private readonly TenantAttributeRegistry? _tenantAttributes;
    private readonly string _onCluster;

    public ClickHouseSchemaMigrator(
        ClickHouseConnectionFactory connectionFactory,
        DimensionRegistry registry,
        MeasureRegistry measures,
        RollupRegistry rollups,
        Microsoft.Extensions.Options.IOptions<TelemetryEngineOptions> options,
        ILogger<ClickHouseSchemaMigrator> logger,
        TenantAttributeRegistry? tenantAttributes = null)
    {
        _tenantAttributes = tenantAttributes;
        _onCluster = ClusterDdl.Clause(options.Value.ClusterName);
        _connectionFactory = connectionFactory;
        _registry = registry;
        _measures = measures;
        _rollups = rollups;
        _retentionDays = options.Value.RetentionDays;
        _backfillRollups = options.Value.BackfillRollups;
        _eventTypeRetention = options.Value.EventTypeRetention ?? [];
        ValidateEventTypeRetention(_retentionDays, _eventTypeRetention);
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

    [LoggerMessage(EventId = 8101, Level = LogLevel.Information,
        Message = "Measure '{Measure}' is declared but missing from {Database}.{Table}. "
            + "Adding it as {ClickHouseType}.")]
    private static partial void LogAddingMeasure(
        ILogger logger, string measure, string database, string table, string clickHouseType);

    [LoggerMessage(EventId = 8103, Level = LogLevel.Critical,
        Message = "SCHEMA REFUSED: measure '{Measure}' is declared as {DeclaredType} but the column "
            + "in {Database}.{Table} is {ActualType}. Refusing to start rather than coerce it — "
            + "narrowing a numeric column truncates every value that no longer fits and widening it "
            + "changes what the old rows meant. Fix the declared type or rename the measure.")]
    private static partial void LogMeasureTypeMismatch(
        ILogger logger, string measure, string declaredType, string database, string table, string actualType);

    [LoggerMessage(EventId = 8105, Level = LogLevel.Information,
        Message = "Measure '{Measure}' is retired: its column and {RowCount} row(s) are kept, "
            + "ingestion refuses the name, and the query API does not offer it.")]
    private static partial void LogRetiredMeasure(ILogger logger, string measure, ulong rowCount);

    [LoggerMessage(EventId = 8110, Level = LogLevel.Information,
        Message = "Retention on {Database}.{Table} is [{Actual}] but configuration declares "
            + "[{Declared}]. Applying the declared value. Existing parts keep their old TTL until "
            + "their next merge, so eviction of already-written data is not immediate.")]
    private static partial void LogRetentionDrift(
        ILogger logger, string database, string table, string actual, string declared);

    [LoggerMessage(EventId = 8111, Level = LogLevel.Warning,
        Message = "Could not read the retention TTL on {Database}.{Table}; leaving it untouched. "
            + "The table keeps whatever TTL it was created with, which may differ from "
            + "TelemetryEngine:RetentionDays.")]
    private static partial void LogRetentionUnreadable(ILogger logger, string database, string table);

    [LoggerMessage(EventId = 8120, Level = LogLevel.Information,
        Message = "Rollup '{Rollup}' created: {Database}.{Table} and its materialized view, "
            + "backfilled from {Partitions} partition(s) of the source table.")]
    private static partial void LogRollupCreated(
        ILogger logger, string rollup, string database, string table, int partitions);

    [LoggerMessage(EventId = 8121, Level = LogLevel.Warning,
        Message = "Rollup '{Rollup}' already exists and its stored definition differs from the "
            + "declared one, so no query is routed to it. Drop {Database}.{View} and "
            + "{Database}.{Table}; the next start recreates and backfills it.")]
    private static partial void LogRollupDrift(
        ILogger logger, string rollup, string database, string view, string table);

    [LoggerMessage(EventId = 8122, Level = LogLevel.Information,
        Message = "Rollup '{Rollup}' created without backfill because TelemetryEngine:BackfillRollups "
            + "is off. It aggregates from this point forward only.")]
    private static partial void LogRollupNotBackfilled(ILogger logger, string rollup);

    [LoggerMessage(EventId = 8123, Level = LogLevel.Information,
        Message = "Rollup '{Rollup}' view was created by another instance; leaving the backfill to it.")]
    private static partial void LogRollupCreatedElsewhere(ILogger logger, string rollup);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SchemaReconciliationException)
        {
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

        foreach (string statement in CoreStatementsFor(_onCluster))
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
                    LogRetiredDimension(_logger, dimension.Name, 0);
                    continue;
                }

                LogAddingDimension(_logger, dimension.Name, Database, Table, dimension.ClickHouseType);

                string ddl =
                    $"ALTER TABLE {Database}.{Table}{_onCluster} ADD COLUMN IF NOT EXISTS "
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

        foreach (Measure measure in _measures.Declared)
        {
            if (!live.TryGetValue(measure.Name, out string? actualType))
            {
                if (measure.Retired)
                {
                    LogRetiredMeasure(_logger, measure.Name, 0);
                    continue;
                }

                LogAddingMeasure(_logger, measure.Name, Database, Table, measure.ClickHouseType);

                await ExecuteAsync(
                    pooled,
                    $"ALTER TABLE {Database}.{Table}{_onCluster} ADD COLUMN IF NOT EXISTS "
                    + $"{measure.Name} {measure.ClickHouseType}",
                    cancellationToken).ConfigureAwait(false);

                added++;
                continue;
            }

            if (!TypesMatch(measure.ClickHouseType, actualType))
            {
                LogMeasureTypeMismatch(
                    _logger, measure.Name, measure.ClickHouseType, Database, Table, actualType);

                throw new SchemaReconciliationException(
                    $"Measure '{measure.Name}' is declared as {measure.ClickHouseType} but the "
                    + $"column in {Database}.{Table} is {actualType}.");
            }

            if (measure.Retired)
            {
                ulong retiredRows =
                    await CountNonNullAsync(pooled, measure.Name, cancellationToken).ConfigureAwait(false);
                LogRetiredMeasure(_logger, measure.Name, retiredRows);
            }
        }

        await ReconcileRetentionAsync(pooled, cancellationToken).ConfigureAwait(false);

        if (_tenantAttributes is { Enabled: true })
        {
            await ExecuteAsync(pooled, TenantAttributeRegistry.BuildTableDdl(_onCluster), cancellationToken)
                .ConfigureAwait(false);
        }

        await ReconcileRollupsAsync(pooled, cancellationToken).ConfigureAwait(false);

        await RefuseUndeclaredColumnsWithDataAsync(pooled, live, cancellationToken).ConfigureAwait(false);

        LogReconciled(
            _logger,
            CoreStatements.Count,
            _registry.Declared.Count + _measures.Declared.Count,
            added);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static bool TypesMatch(string declared, string actual) =>
        string.Equals(Normalize(declared), Normalize(actual), StringComparison.Ordinal);

    private static string Normalize(string type) => type.Replace(" ", string.Empty, StringComparison.Ordinal);

    private async Task RefuseUndeclaredColumnsWithDataAsync(
        PooledClickHouseConnection pooled,
        Dictionary<string, string> live,
        CancellationToken cancellationToken)
    {
        List<string> offenders = [];

        foreach (string column in live.Keys.OrderBy(name => name, StringComparer.Ordinal))
        {
            if (DimensionRegistry.ReservedColumns.Contains(column)
                || _registry.IsDeclared(column)
                || _measures.IsDeclared(column))
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
                + $"{string.Join(", ", offenders)}. Declare them under TelemetryEngine:Dimensions or "
                + "TelemetryEngine:Measures, with Retired: true if collection should stop.");
        }
    }

    private async Task ReconcileRollupsAsync(
        PooledClickHouseConnection pooled, CancellationToken cancellationToken)
    {
        foreach (Rollup rollup in _rollups.Declared)
        {
            string desiredView = RollupRegistry.BuildViewDdl(rollup, _onCluster);

            string? existing = await ReadCreateQueryAsync(
                pooled, rollup.ViewName, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                await ExecuteAsync(
                    pooled, RollupRegistry.BuildTableDdl(rollup, _onCluster), cancellationToken).ConfigureAwait(false);

                if (!await TryCreateViewAsync(pooled, desiredView, cancellationToken).ConfigureAwait(false))
                {
                    LogRollupCreatedElsewhere(_logger, rollup.Name);
                    _rollups.MarkRoutable(rollup);
                    continue;
                }

                if (!_backfillRollups)
                {
                    LogRollupNotBackfilled(_logger, rollup.Name);
                    _rollups.MarkRoutable(rollup);
                    continue;
                }

                int partitions = await BackfillRollupAsync(pooled, rollup, cancellationToken).ConfigureAwait(false);
                LogRollupCreated(_logger, rollup.Name, Database, rollup.TableName, partitions);
                _rollups.MarkRoutable(rollup);
                continue;
            }

            if (!DefinitionsAgree(existing, rollup))
            {
                _rollups.MarkUnroutable(rollup, "stored definition differs from the declared one");
                LogRollupDrift(_logger, rollup.Name, Database, rollup.ViewName, rollup.TableName);
                continue;
            }

            _rollups.MarkRoutable(rollup);
        }
    }

    private const int TableAlreadyExistsCode = 57;

    private static async Task<bool> TryCreateViewAsync(
        PooledClickHouseConnection pooled, string ddl, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(pooled, ddl, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ClickHouseServerException ex) when (ex.ServerErrorCode == TableAlreadyExistsCode)
        {
            return false;
        }
    }

    private static async Task<int> BackfillRollupAsync(
        PooledClickHouseConnection pooled, Rollup rollup, CancellationToken cancellationToken)
    {
        List<string> partitions = await ReadSourcePartitionsAsync(pooled, cancellationToken).ConfigureAwait(false);

        foreach (string partition in partitions)
        {
            string? scope = RollupRegistry.IsPartitionId(partition) ? partition : null;
            await ExecuteAsync(pooled, RollupRegistry.BuildBackfillSql(rollup, scope), cancellationToken)
                .ConfigureAwait(false);

            if (scope is null)
            {
                return 1;
            }
        }

        return partitions.Count;
    }

    private static async Task<List<string>> ReadSourcePartitionsAsync(
        PooledClickHouseConnection pooled, CancellationToken cancellationToken)
    {
        await using ClickHouseCommand command = pooled.Connection.CreateCommand(
            "SELECT DISTINCT partition FROM system.parts "
            + "WHERE database = {database:String} AND table = {table:String} AND active ORDER BY partition");
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "database", Value = Database });
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "table", Value = Table });

        List<string> partitions = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            partitions.Add(reader.GetString(0));
        }

        return partitions;
    }

    internal static bool DefinitionsAgree(string storedCreateQuery, Rollup rollup)
    {
        if (!storedCreateQuery.Contains(RollupRegistry.TrafficColumn, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string attribute in rollup.TenantAttributes)
        {
            if (!storedCreateQuery.Contains(RollupRegistry.TenantAttributeColumn(attribute), StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            if (!storedCreateQuery.Contains(dimension.Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (RollupMeasure measure in rollup.Measures)
        {
            if (!storedCreateQuery.Contains(measure.ColumnName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return storedCreateQuery.Contains(rollup.BucketFunction, StringComparison.Ordinal);
    }

    private async Task<string?> ReadCreateQueryAsync(
        PooledClickHouseConnection pooled, string table, CancellationToken cancellationToken)
    {
        await using ClickHouseCommand command = pooled.Connection.CreateCommand(
            "SELECT create_table_query FROM system.tables "
            + "WHERE database = {database:String} AND name = {table:String}");

        command.Parameters.Add(new ClickHouseParameter { ParameterName = "database", Value = Database });
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "table", Value = table });

        object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw as string;
    }

    internal static string BuildRetentionDdl(int retentionDays) =>
        BuildRetentionDdl(retentionDays, []);

    internal static string BuildRetentionDdl(
        int retentionDays, IReadOnlyList<EventTypeRetentionOptions> perEventType) =>
        BuildRetentionDdl(retentionDays, perEventType, string.Empty);

    internal static string BuildRetentionDdl(
        int retentionDays, IReadOnlyList<EventTypeRetentionOptions> perEventType, string onCluster)
    {
        StringBuilder ttl = new(128);

        foreach (EventTypeRetentionOptions rule in perEventType)
        {
            ttl.Append("toDateTime(timestamp) + INTERVAL ").Append(rule.Days).Append(" DAY DELETE");
            ttl.Append(" WHERE event_type = '").Append(rule.EventType).Append("', ");
        }

        ttl.Append("toDateTime(timestamp) + INTERVAL ").Append(retentionDays).Append(" DAY DELETE");

        return $"ALTER TABLE {Database}.{Table}{onCluster} MODIFY TTL {ttl} "
            + "SETTINGS materialize_ttl_after_modify = 0";
    }

    internal static string? ReadRetentionDays(string? createTableQuery)
    {
        if (string.IsNullOrEmpty(createTableQuery))
        {
            return null;
        }

        Match match = RetentionPattern.Match(createTableQuery);

        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    internal static List<(int Days, string? EventType)>? ReadRetentionRules(string? createTableQuery)
    {
        if (string.IsNullOrEmpty(createTableQuery))
        {
            return null;
        }

        int ttlAt = createTableQuery.IndexOf("\nTTL ", StringComparison.Ordinal);
        if (ttlAt < 0)
        {
            ttlAt = createTableQuery.IndexOf(" TTL ", StringComparison.Ordinal);
        }

        if (ttlAt < 0)
        {
            return null;
        }

        int end = createTableQuery.IndexOf("\nSETTINGS", ttlAt, StringComparison.Ordinal);
        string ttlText = end < 0 ? createTableQuery[ttlAt..] : createTableQuery[ttlAt..end];

        List<(int, string?)> rules = [];

        foreach (Match match in RetentionRulePattern.Matches(ttlText))
        {
            string days = match.Groups["d1"].Success ? match.Groups["d1"].Value : match.Groups["d2"].Value;

            if (!int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                continue;
            }

            rules.Add((parsed, match.Groups["event"].Success ? match.Groups["event"].Value : null));
        }

        return rules.Count == 0 ? null : rules;
    }

    internal static bool RetentionAgrees(
        List<(int Days, string? EventType)> actual,
        int retentionDays,
        IReadOnlyList<EventTypeRetentionOptions> perEventType)
    {
        HashSet<(int, string?)> declared = [(retentionDays, null)];

        foreach (EventTypeRetentionOptions rule in perEventType)
        {
            declared.Add((rule.Days, rule.EventType));
        }

        return declared.SetEquals(actual);
    }

    internal static void ValidateEventTypeRetention(
        int retentionDays, IReadOnlyList<EventTypeRetentionOptions> perEventType)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (EventTypeRetentionOptions rule in perEventType)
        {
            string eventType = rule.EventType ?? string.Empty;

            if (!EventTypeNamePattern.IsMatch(eventType))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:EventTypeRetention declares event type '{eventType}', which is "
                    + "not a valid name. It must start with a letter and contain only letters, digits, "
                    + "'_', '.', ':' or '-'. The name is written into a TTL condition, so nothing else "
                    + "can be permitted here.");
            }

            if (!seen.Add(eventType))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:EventTypeRetention declares '{rule.EventType}' more than once. "
                    + "ClickHouse would keep both clauses and apply the shorter, so the longer entry "
                    + "would be a line of configuration that does nothing.");
            }

            if (rule.Days <= 0)
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:EventTypeRetention gives '{rule.EventType}' {rule.Days} day(s). "
                    + "It must be at least 1; to stop collecting an event type, stop sending it.");
            }

            if (rule.Days >= retentionDays)
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:EventTypeRetention keeps '{rule.EventType}' for {rule.Days} day(s), "
                    + $"which is not shorter than TelemetryEngine:RetentionDays ({retentionDays}). A "
                    + "compound TTL applies the shortest matching rule, so this entry would change "
                    + "nothing while appearing to: the rows would still be deleted after "
                    + $"{retentionDays} day(s). Raise RetentionDays instead.");
            }
        }
    }

    private async Task ReconcileRetentionAsync(
        PooledClickHouseConnection pooled, CancellationToken cancellationToken)
    {
        if (_retentionDays <= 0)
        {
            return;
        }

        await using ClickHouseCommand command = pooled.Connection.CreateCommand(
            "SELECT create_table_query FROM system.tables "
            + "WHERE database = {database:String} AND name = {table:String}");

        command.Parameters.Add(new ClickHouseParameter { ParameterName = "database", Value = Database });
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "table", Value = Table });

        object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        List<(int Days, string? EventType)>? actual = ReadRetentionRules(raw as string);

        if (actual is null)
        {
            LogRetentionUnreadable(_logger, Database, Table);
            return;
        }

        if (RetentionAgrees(actual, _retentionDays, _eventTypeRetention))
        {
            return;
        }

        LogRetentionDrift(_logger, Database, Table, Describe(actual), Describe(_retentionDays, _eventTypeRetention));
        await ExecuteAsync(
            pooled, BuildRetentionDdl(_retentionDays, _eventTypeRetention, _onCluster), cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(List<(int Days, string? EventType)> rules) =>
        string.Join(", ", rules.Select(r => r.EventType is null ? $"{r.Days}d" : $"{r.Days}d for {r.EventType}"));

    private static string Describe(int retentionDays, IReadOnlyList<EventTypeRetentionOptions> perEventType) =>
        string.Join(
            ", ",
            perEventType.Select(r => $"{r.Days}d for {r.EventType}").Append($"{retentionDays}d"));

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

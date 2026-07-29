namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Octonica.ClickHouseClient;

/// <summary>
/// Brings <c>global_events</c> up to the shape the ingest path writes.
///
/// <c>clickhouse-init.sql</c> is mounted into <c>docker-entrypoint-initdb.d</c>, so it runs
/// exactly once, against an empty data volume. Any deployment that already has data never sees
/// it — which means schema changes shipped that way silently do not apply, and the first insert
/// naming a new column fails the whole batch.
///
/// Every statement here is idempotent (<c>ADD COLUMN IF NOT EXISTS</c>), so this is safe to run
/// on every start, on a fresh volume or an old one.
///
/// It runs before the batch writer takes traffic: a missing column would otherwise reject entire
/// batches rather than one row, and telemetry has no retry once the beacon returns.
/// </summary>
public sealed class ClickHouseSchemaMigrator : IHostedService
{
    /// <summary>
    /// Kept in step with <c>clickhouse-init.sql</c> by hand — the two describe the same table.
    /// Nullable for identifiers (absent on events that have no such dimension) and
    /// LowCardinality with an empty default for the edge-derived attributes, which have a small
    /// fixed vocabulary and should never be null in a GROUP BY.
    /// </summary>
    private static readonly IReadOnlyList<string> Statements =
    [
        "ALTER TABLE nealytics_core.global_events ADD COLUMN IF NOT EXISTS menu_id Nullable(String)",
        "ALTER TABLE nealytics_core.global_events ADD COLUMN IF NOT EXISTS section_id Nullable(String)",
        "ALTER TABLE nealytics_core.global_events ADD COLUMN IF NOT EXISTS table_id Nullable(String)",
        "ALTER TABLE nealytics_core.global_events ADD COLUMN IF NOT EXISTS device_class LowCardinality(String) DEFAULT ''",
        "ALTER TABLE nealytics_core.global_events ADD COLUMN IF NOT EXISTS os LowCardinality(String) DEFAULT ''",
        "ALTER TABLE nealytics_core.global_events ADD COLUMN IF NOT EXISTS browser LowCardinality(String) DEFAULT ''",
        "ALTER TABLE nealytics_core.global_events ADD COLUMN IF NOT EXISTS country LowCardinality(String) DEFAULT ''",
    ];

    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly ILogger<ClickHouseSchemaMigrator> _logger;

    public ClickHouseSchemaMigrator(
        ClickHouseConnectionFactory connectionFactory,
        ILogger<ClickHouseSchemaMigrator> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using PooledClickHouseConnection pooled =
                await _connectionFactory.AcquireAsync(cancellationToken).ConfigureAwait(false);

            foreach (string statement in Statements)
            {
                await using ClickHouseCommand command = pooled.Connection.CreateCommand(statement);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "ClickHouse schema verified — {StatementCount} idempotent statements applied.",
                Statements.Count);
        }
        catch (Exception ex)
        {
            // Deliberately not fatal. The query endpoints stay useful against whatever columns
            // do exist, and a hard failure here would take down analytics reads because a write
            // path could not be widened.
            _logger.LogError(ex,
                "ClickHouse schema migration failed. Ingest will reject batches naming the new "
                + "columns until this succeeds.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

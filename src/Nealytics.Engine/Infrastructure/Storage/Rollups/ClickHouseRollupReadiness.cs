namespace Nealytics.Engine.Infrastructure.Storage.Rollups;

using System;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Configuration;
using Octonica.ClickHouseClient;

internal sealed class ClickHouseRollupReadiness
{
    internal const string TableName = "nealytics_core.rollup_readiness";
    private readonly PooledClickHouseConnection _pooled;

    public ClickHouseRollupReadiness(PooledClickHouseConnection pooled) => _pooled = pooled;

    public async Task EnsureCreatedAsync(string onCluster, CancellationToken cancellationToken)
    {
        await using ClickHouseCommand command = _pooled.Connection.CreateCommand(
            $"CREATE TABLE IF NOT EXISTS {TableName}{onCluster} ("
            + "source_id UUID, table_id UUID, view_id UUID, declared_definition FixedString(64), "
            + "stored_definition FixedString(64), backfilled UInt8, completed_at DateTime64(3, 'UTC')) "
            + "ENGINE = ReplacingMergeTree ORDER BY "
            + "(source_id, table_id, view_id, declared_definition, stored_definition, backfilled)");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RollupStorageIdentity> CaptureAsync(Rollup rollup, CancellationToken cancellationToken)
    {
        await using ClickHouseCommand command = _pooled.Connection.CreateCommand(
            "SELECT name, uuid, create_table_query FROM system.tables WHERE database = {database:String} "
            + "AND name IN ({source:String}, {table:String}, {view:String})");
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "database", Value = RollupRegistry.Database });
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "source", Value = RollupRegistry.SourceTable });
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "table", Value = rollup.TableName });
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "view", Value = rollup.ViewName });
        Guid sourceId = Guid.Empty;
        Guid tableId = Guid.Empty;
        Guid viewId = Guid.Empty;
        string? tableDefinition = null;
        string? viewDefinition = null;
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string name = reader.GetString(0);
            Guid identity = reader.GetGuid(1);
            if (name == RollupRegistry.SourceTable)
            {
                sourceId = identity;
            }
            else if (name == rollup.TableName)
            {
                tableId = identity;
                tableDefinition = reader.GetString(2);
            }
            else
            {
                viewId = identity;
                viewDefinition = reader.GetString(2);
            }
        }
        if (tableDefinition is null || viewDefinition is null)
        {
            throw new InvalidOperationException("Rollup '" + rollup.Name + "' is missing its table or view.");
        }
        return new RollupStorageIdentity(sourceId, tableId, viewId,
            Fingerprint(RollupRegistry.BuildTableDdl(rollup), RollupRegistry.BuildViewDdl(rollup)),
            Fingerprint(tableDefinition, viewDefinition));
    }

    public async Task<bool> IsCompleteAsync(
        RollupStorageIdentity identity, bool requireBackfill, CancellationToken cancellationToken)
    {
        await using ClickHouseCommand command = _pooled.Connection.CreateCommand(
            $"SELECT count() FROM {TableName} WHERE {IdentityPredicate} "
            + "AND (backfilled = 1 OR {require_backfill:UInt8} = 0)");
        AddIdentity(command, identity);
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "require_backfill", Value = requireBackfill ? (byte)1 : (byte)0 });
        object? count = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture) > 0;
    }

    public async Task CompleteAsync(
        RollupStorageIdentity identity, bool backfilled, CancellationToken cancellationToken)
    {
        await using ClickHouseCommand command = _pooled.Connection.CreateCommand(
            $"INSERT INTO {TableName} (source_id, table_id, view_id, declared_definition, stored_definition, backfilled, completed_at) "
            + "VALUES ({source_id:UUID}, {table_id:UUID}, {view_id:UUID}, {declared_definition:FixedString(64)}, "
            + "{stored_definition:FixedString(64)}, {backfilled:UInt8}, now64(3))");
        AddIdentity(command, identity);
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "backfilled", Value = backfilled ? (byte)1 : (byte)0 });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string Fingerprint(string table, string view) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(table + "\0" + view)));

    private const string IdentityPredicate = "source_id = {source_id:UUID} AND table_id = {table_id:UUID} "
        + "AND view_id = {view_id:UUID} AND declared_definition = {declared_definition:FixedString(64)} "
        + "AND stored_definition = {stored_definition:FixedString(64)}";

    private static void AddIdentity(ClickHouseCommand command, RollupStorageIdentity identity)
    {
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "source_id", Value = identity.SourceId });
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "table_id", Value = identity.TableId });
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "view_id", Value = identity.ViewId });
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "declared_definition", Value = identity.DeclaredDefinition });
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "stored_definition", Value = identity.StoredDefinition });
    }
}

namespace Nealytics.Engine.Features.GetSchema;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed class GetColumnPopulationQuery
{
    internal const int LookbackDays = 30;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly DimensionRegistry _dimensions;
    private readonly MeasureRegistry _measures;

    private readonly ConcurrentDictionary<string, (DateTime ExpiresAt, IReadOnlyDictionary<string, long> Counts)> _cache =
        new(StringComparer.Ordinal);

    public GetColumnPopulationQuery(
        ClickHouseConnectionFactory connectionFactory,
        DimensionRegistry dimensions,
        MeasureRegistry measures)
    {
        _connectionFactory = connectionFactory;
        _dimensions = dimensions;
        _measures = measures;
    }

    internal static string CountExpression(string column, bool stringLike) =>
        stringLike
            ? $"countIf(coalesce({column}, '') != '') AS {column}"
            : $"count({column}) AS {column}";

    internal static bool IsStringLike(DimensionValueKind kind) =>
        kind is DimensionValueKind.String or DimensionValueKind.LowCardinalityString;

    internal string BuildSql()
    {
        StringBuilder select = new();

        foreach (Dimension dimension in _dimensions.Active)
        {
            if (select.Length > 0) select.Append(", ");
            select.Append(CountExpression(dimension.Name, IsStringLike(dimension.Kind)));
        }

        foreach (Measure measure in _measures.Active)
        {
            if (select.Length > 0) select.Append(", ");
            select.Append(CountExpression(measure.Name, stringLike: false));
        }

        if (select.Length == 0) return string.Empty;

        return "SELECT " + select
            + " FROM nealytics_core.global_events"
            + " WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}"
            + " AND timestamp >= {fromTimestamp:DateTime64}";
    }

    internal string BuildSql(TenantSet? set)
    {
        string sql = BuildSql();

        return set is null || sql.Length == 0
            ? sql
            : sql.Replace("tenant_id = {tenantId:String}", "tenant_id IN (" + ScopeClause.TenantMembers + ")", StringComparison.Ordinal);
    }

    public Task<IReadOnlyDictionary<string, long>> ExecuteAsync(
        string projectId, string tenantId, DateTime nowUtc, CancellationToken cancellationToken) =>
        ExecuteAsync(projectId, tenantId, null, nowUtc, cancellationToken);

    public async Task<IReadOnlyDictionary<string, long>> ExecuteAsync(
        string projectId, string tenantId, TenantSet? set, DateTime nowUtc, CancellationToken cancellationToken)
    {
        string key = projectId + " " + (set is TenantSet members ? "\0set " + members.Describe() : tenantId);

        if (_cache.TryGetValue(key, out (DateTime ExpiresAt, IReadOnlyDictionary<string, long> Counts) cached)
            && cached.ExpiresAt > nowUtc)
        {
            return cached.Counts;
        }

        string sql = BuildSql(set);
        Dictionary<string, long> counts = new(StringComparer.Ordinal);

        if (sql.Length == 0)
        {
            _cache[key] = (nowUtc.Add(CacheLifetime), counts);
            return counts;
        }

        await using PooledClickHouseConnection lease =
            await _connectionFactory.AcquireAsync(cancellationToken);

        await using ClickHouseCommand command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "projectId", Value = projectId });
        command.Parameters.Add(new ClickHouseParameter { ParameterName = "tenantId", Value = tenantId });

        if (set is TenantSet scoped)
        {
            command.Parameters.Add(new ClickHouseParameter { ParameterName = "tenantSetAttribute", Value = scoped.Attribute });
            command.Parameters.Add(new ClickHouseParameter { ParameterName = "tenantSetValue", Value = scoped.Value });
        }
        command.Parameters.Add(new ClickHouseParameter
        {
            ParameterName = "fromTimestamp",
            Value = nowUtc.AddDays(-LookbackDays),
        });

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                counts[reader.GetName(i)] =
                    Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);
            }
        }

        _cache[key] = (nowUtc.Add(CacheLifetime), counts);
        return counts;
    }
}

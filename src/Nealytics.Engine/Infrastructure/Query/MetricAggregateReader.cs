namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

internal sealed record MetricAggregateRow(
    DateTime? Bucket, IReadOnlyList<string?> Groups, IReadOnlyDictionary<string, string?> Values);

internal sealed class MetricAggregateReader
{
    private readonly ClickHouseConnectionFactory _connections;

    internal MetricAggregateReader(ClickHouseConnectionFactory connections) => _connections = connections;

    internal async Task<IReadOnlyDictionary<string, string?>> ReadAsync(
        string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters,
        IReadOnlyList<NamedMetric> components, CancellationToken cancellationToken)
    {
        IReadOnlyList<MetricAggregateRow> rows = await ReadRowsAsync(sql, parameters, components, 0, false, 1, cancellationToken);
        return rows[0].Values;
    }

    internal async Task<IReadOnlyList<MetricAggregateRow>> ReadRowsAsync(
        string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters,
        IReadOnlyList<NamedMetric> components, int groupCount, bool hasBucket, int limit, CancellationToken cancellationToken)
    {
        await using PooledClickHouseConnection lease = await _connections.AcquireAsync(cancellationToken);
        await using ClickHouseCommand command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (KeyValuePair<string, object?> parameter in parameters)
        {
            ClickHouseParameter bound = new ClickHouseParameter
            {
                ParameterName = parameter.Key,
                Value = parameter.Value
            };
            if (parameter.Value is DateTime)
            {
                bound.ClickHouseDbType = ClickHouseDbType.DateTime2;
            }
            command.Parameters.Add(bound);
        }
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<MetricAggregateRow> rows = [];
        int offset = groupCount + (hasBucket ? 1 : 0);
        while (rows.Count < limit && await reader.ReadAsync(cancellationToken))
        {
            DateTime? bucket = hasBucket ? DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc) : null;
            string?[] groups = new string?[groupCount];
            for (int index = 0; index < groupCount; index++)
            {
                int ordinal = index + (hasBucket ? 1 : 0);
                groups[index] = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            }
            Dictionary<string, string?> values = new(components.Count, StringComparer.Ordinal);
            for (int index = 0; index < components.Count; index++)
            {
                int ordinal = offset + index;
                string? value = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
                values.Add(components[index].Name, MetricProjection.NormalizeValue(value));
            }
            rows.Add(new MetricAggregateRow(bucket, Array.AsReadOnly(groups), values));
        }
        return Array.AsReadOnly(rows.ToArray());
    }
}

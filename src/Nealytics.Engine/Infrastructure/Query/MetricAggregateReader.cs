namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

internal sealed class MetricAggregateReader
{
    private readonly ClickHouseConnectionFactory _connections;

    internal MetricAggregateReader(ClickHouseConnectionFactory connections) => _connections = connections;

    internal async Task<IReadOnlyDictionary<string, string?>> ReadAsync(
        string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters,
        IReadOnlyList<NamedMetric> components, CancellationToken cancellationToken)
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
        await reader.ReadAsync(cancellationToken);
        Dictionary<string, string?> values = new(components.Count, StringComparer.Ordinal);
        for (int index = 0; index < components.Count; index++)
        {
            string? value = reader.IsDBNull(index) ? null : reader.GetString(index);
            values.Add(components[index].Name, MetricProjection.NormalizeValue(value));
        }
        return values;
    }
}

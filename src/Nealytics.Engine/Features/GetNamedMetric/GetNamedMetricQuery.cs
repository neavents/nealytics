namespace Nealytics.Engine.Features.GetNamedMetric;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed class GetNamedMetricQuery
{
    private readonly ClickHouseConnectionFactory _connections;

    public GetNamedMetricQuery(ClickHouseConnectionFactory connections) => _connections = connections;

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(NamedMetricRequest request)
    {
        NamedMetric definition = request.Definition;
        string function = definition.Aggregation;
        if (function is "min" or "max" or "avg")
        {
            function += "OrNull";
        }
        string? column = definition.Measure;
        if (definition.WidenSum)
        {
            column = "toInt256(" + column + ")";
        }
        StringBuilder sql = new StringBuilder("SELECT toString(");
        if (definition.Aggregation == "sum")
        {
            sql.Append("coalesce(");
        }
        sql.Append(function).Append('(').Append(column).Append(')');
        if (definition.Aggregation == "sum")
        {
            sql.Append(", 0)");
        }
        sql.Append(") FROM nealytics_core.global_events");
        ScopeClause.AppendRaw(sql, request.Scope, exclusiveEnd: true);
        return (sql.ToString(), ScopeClause.Parameters(request.Scope));
    }

    public async Task<NamedMetricResponse> ExecuteAsync(NamedMetricRequest request, CancellationToken cancellationToken)
    {
        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = BuildQuery(request);
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
        string? value = reader.IsDBNull(0) ? null : reader.GetString(0);
        return new NamedMetricResponse
        {
            Name = request.Definition.Name,
            Version = request.Definition.Version,
            From = request.Scope.From,
            To = request.Scope.To,
            Value = NormalizeValue(value),
            Unit = request.Definition.Unit
        };
    }

    internal static string? NormalizeValue(string? value) => value is "nan" or "inf" or "-inf" ? null : value;
}

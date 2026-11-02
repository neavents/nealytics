namespace Nealytics.Engine.Features.GetSavedReport;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Features.GetNamedMetric;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed class GetSavedReportQuery
{
    private readonly ClickHouseConnectionFactory _connections;

    public GetSavedReportQuery(ClickHouseConnectionFactory connections) => _connections = connections;

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(SavedReportRequest request)
    {
        StringBuilder sql = new StringBuilder("SELECT ");
        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(request.Scope);
        IReadOnlyList<SavedReportField> fields = request.Definition.Fields;
        for (int index = 0; index < fields.Count; index++)
        {
            if (index != 0)
            {
                sql.Append(", ");
            }
            NamedMetric metric = fields[index].Definition;
            int? conditionIndex = null;
            if (metric.EventType is string eventType)
            {
                conditionIndex = index;
                parameters.Add(new KeyValuePair<string, object?>(MetricAggregateSql.EventTypeParameter(index), eventType));
            }
            MetricAggregateSql.Append(sql, metric, conditionIndex);
        }
        sql.Append(" FROM nealytics_core.global_events");
        ScopeClause.AppendRaw(sql, request.Scope, exclusiveEnd: true);
        return (sql.ToString(), parameters);
    }

    public async Task<SavedReportResponse> ExecuteAsync(SavedReportRequest request, CancellationToken cancellationToken)
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
        SavedReportValue[] values = new SavedReportValue[request.Definition.Fields.Count];
        for (int index = 0; index < values.Length; index++)
        {
            SavedReportField field = request.Definition.Fields[index];
            string? value = reader.IsDBNull(index) ? null : reader.GetString(index);
            values[index] = new SavedReportValue
            {
                Name = field.Name,
                Metric = field.Metric,
                Value = GetNamedMetricQuery.NormalizeValue(value),
                Unit = field.Definition.Unit
            };
        }
        return new SavedReportResponse
        {
            Name = request.Definition.Name,
            Version = request.Definition.Version,
            From = request.Scope.From,
            To = request.Scope.To,
            Segment = request.Definition.Segment,
            Fields = Array.AsReadOnly(values)
        };
    }
}

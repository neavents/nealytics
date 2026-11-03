namespace Nealytics.Engine.Features.GetSavedReport;

using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;

public sealed class GetSavedReportQuery
{
    private readonly MetricAggregateReader _reader;

    public GetSavedReportQuery(ClickHouseConnectionFactory connections) => _reader = new MetricAggregateReader(connections);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(SavedReportRequest request)
    {
        StringBuilder sql = new StringBuilder("SELECT ");
        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(request.Scope);
        MetricProjection projection = new MetricProjection(request.Definition.Fields.Select(field => field.Definition));
        MetricProjectionSql.Append(sql, projection.Components, parameters, conditional: true);
        sql.Append(" FROM nealytics_core.global_events");
        ScopeClause.AppendRaw(sql, request.Scope, exclusiveEnd: true);
        return (sql.ToString(), parameters);
    }

    public async Task<SavedReportResponse> ExecuteAsync(SavedReportRequest request, CancellationToken cancellationToken)
    {
        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = BuildQuery(request);
        MetricProjection projection = new MetricProjection(request.Definition.Fields.Select(field => field.Definition));
        IReadOnlyDictionary<string, string?> components = await _reader.ReadAsync(
            sql, parameters, projection.Components, cancellationToken);
        SavedReportValue[] values = new SavedReportValue[request.Definition.Fields.Count];
        for (int index = 0; index < values.Length; index++)
        {
            SavedReportField field = request.Definition.Fields[index];
            values[index] = new SavedReportValue
            {
                Name = field.Name,
                Metric = field.Metric,
                Value = MetricProjection.Evaluate(field.Definition, components),
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

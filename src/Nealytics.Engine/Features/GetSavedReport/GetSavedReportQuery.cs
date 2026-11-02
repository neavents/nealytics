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
        ReportShapeSql.AppendKeys(sql, request.Definition);
        MetricProjectionSql.Append(sql, projection.Components, parameters, conditional: true);
        sql.Append(" FROM nealytics_core.global_events");
        ScopeClause.AppendRaw(sql, request.Scope, exclusiveEnd: true);
        ReportShapeSql.AppendGrouping(sql, request.Definition);
        return (sql.ToString(), parameters);
    }

    public async Task<SavedReportResponse> ExecuteAsync(SavedReportRequest request, CancellationToken cancellationToken)
    {
        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = BuildQuery(request);
        MetricProjection projection = new MetricProjection(request.Definition.Fields.Select(field => field.Definition));
        SavedReport definition = request.Definition;
        IReadOnlyList<MetricAggregateRow> selected = await _reader.ReadRowsAsync(
            sql, parameters, projection.Components, definition.GroupBy.Count, definition.Interval is not null,
            definition.IsGrouped ? definition.Limit : 1, cancellationToken);
        SavedReportRow[] rows = definition.IsGrouped ? new SavedReportRow[selected.Count] : Array.Empty<SavedReportRow>();
        for (int index = 0; index < rows.Length; index++)
        {
            MetricAggregateRow row = selected[index];
            SavedReportGroup[] groups = new SavedReportGroup[definition.GroupBy.Count];
            for (int group = 0; group < groups.Length; group++)
            {
                groups[group] = new SavedReportGroup { Name = definition.GroupBy[group], Value = row.Groups[group] };
            }
            rows[index] = new SavedReportRow
            {
                Bucket = row.Bucket,
                Groups = Array.AsReadOnly(groups),
                Fields = Values(definition, row.Values)
            };
        }
        return new SavedReportResponse
        {
            Name = definition.Name,
            Version = definition.Version,
            From = request.Scope.From,
            To = request.Scope.To,
            Segment = definition.Segment,
            Shape = definition.Interval is not null ? "timeseries" : definition.IsGrouped ? "table" : "scalar",
            Fields = definition.IsGrouped ? Array.Empty<SavedReportValue>() : Values(definition, selected[0].Values),
            Rows = Array.AsReadOnly(rows)
        };
    }

    private static IReadOnlyList<SavedReportValue> Values(SavedReport definition, IReadOnlyDictionary<string, string?> components)
    {
        SavedReportValue[] values = new SavedReportValue[definition.Fields.Count];
        for (int index = 0; index < values.Length; index++)
        {
            SavedReportField field = definition.Fields[index];
            values[index] = new SavedReportValue
            {
                Name = field.Name,
                Metric = field.Metric,
                Value = MetricProjection.Evaluate(field.Definition, components),
                Unit = field.Definition.Unit
            };
        }
        return Array.AsReadOnly(values);
    }
}

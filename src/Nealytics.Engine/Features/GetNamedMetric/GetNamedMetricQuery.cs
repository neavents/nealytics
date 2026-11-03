namespace Nealytics.Engine.Features.GetNamedMetric;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;

public sealed class GetNamedMetricQuery
{
    private readonly MetricAggregateReader _reader;
    private readonly AnalyticsPlanner? _planner;

    public GetNamedMetricQuery(ClickHouseConnectionFactory connections, AnalyticsPlanner? planner = null)
    {
        _reader = new MetricAggregateReader(connections);
        _planner = planner;
    }

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(NamedMetricRequest request)
    {
        StringBuilder sql = new StringBuilder("SELECT ");
        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(request.Scope);
        MetricProjection projection = new MetricProjection([request.Definition]);
        MetricProjectionSql.Append(sql, projection.Components, parameters, request.Definition.CompiledExpression is not null);
        sql.Append(" FROM nealytics_core.global_events");
        ScopeClause.AppendRaw(sql, request.Scope, exclusiveEnd: true);
        return (sql.ToString(), parameters);
    }

    public async Task<NamedMetricResponse> ExecuteAsync(NamedMetricRequest request, CancellationToken cancellationToken)
    {
        MetricProjection projection = new MetricProjection([request.Definition]);
        AnalyticsPlan? plan = _planner?.Select(projection.Components, request.Scope, null, request.Execution);
        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = plan?.Rollup is null
            ? BuildQuery(request) : AnalyticsPlanSql.Build(plan, projection.Components, request.Scope, null);
        IReadOnlyDictionary<string, string?> values = await _reader.ReadAsync(
            sql, parameters, projection.Components, cancellationToken);
        return new NamedMetricResponse
        {
            Name = request.Definition.Name,
            Version = request.Definition.Version,
            From = request.Scope.From,
            To = request.Scope.To,
            Value = MetricProjection.Evaluate(request.Definition, values),
            Unit = request.Definition.Unit,
            Segment = request.Segment,
            Source = plan?.Source ?? "raw",
            Accuracy = request.Definition.Accuracy
        };
    }

    internal static string? NormalizeValue(string? value) => MetricProjection.NormalizeValue(value);
}

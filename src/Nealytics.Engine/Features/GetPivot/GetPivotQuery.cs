namespace Nealytics.Engine.Features.GetPivot;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class GetPivotQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly RollupRegistry _rollups;
    private readonly ILogger<GetPivotQuery> _logger;

    public GetPivotQuery(
        ClickHouseConnectionFactory connectionFactory, RollupRegistry rollups, ILogger<GetPivotQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _rollups = rollups;
        _logger = logger;
    }

    [LoggerMessage(EventId = 6201, Level = LogLevel.Information,
        Message = "Executing pivot for Project: {ProjectId} / Tenant: {TenantId} by {GroupBy}, {MetricCount} metric(s).")]
    private static partial void LogQueryStarted(
        ILogger logger, string projectId, string tenantId, string groupBy, int metricCount);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in PivotRequest request) => BuildQuery(request, null);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in PivotRequest request, PivotRollupPlan? plan)
    {
        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(request.Scope, 24);
        parameters.Add(new KeyValuePair<string, object?>("limit", request.Limit));
        TenantGrouping.AddParameter(parameters, request.GroupByColumn, plan?.Rollup);

        for (int i = 0; i < request.Metrics.Count; i++)
        {
            string? eventType = request.Metrics[i].EventType;

            if (eventType is not null)
            {
                parameters.Add(new KeyValuePair<string, object?>(EventTypeParameter(i), eventType));
            }
        }

        StringBuilder aggregates = new(256);

        for (int i = 0; i < request.Metrics.Count; i++)
        {
            if (i > 0)
            {
                aggregates.Append(", ");
            }

            aggregates.Append(plan is PivotRollupPlan rollup
                ? RollupExpression(request.Metrics[i], rollup.StateColumns[i], i)
                : RawExpression(request, request.Metrics[i], i));
            aggregates.Append(" AS m").Append(i);
        }

        StringBuilder where = new(256);
        string source;

        if (plan is PivotRollupPlan routed)
        {
            source = RollupRegistry.Database + "." + routed.Rollup.TableName;
            ScopeClause.AppendRollup(where, request.Scope, "bucket", routed.Rollup);
        }
        else
        {
            source = RollupRegistry.Database + "." + RollupRegistry.SourceTable;
            ScopeClause.AppendRaw(where, request.Scope);
        }

        StringBuilder sql = new(1024);
        sql.Append(TenantGrouping.With(request.GroupByColumn, plan?.Rollup));
        sql.Append("grouped AS (SELECT ");
        sql.Append(plan is PivotRollupPlan keyed
            ? TenantGrouping.RollupKey(request.GroupByColumn, keyed.Rollup)
            : TenantGrouping.RawKey(request.GroupByColumn));
        sql.Append(" AS key, ").Append(aggregates).Append(" FROM ").Append(source).Append(where).Append(" GROUP BY key),");
        sql.Append(" totals AS (SELECT ").Append(aggregates).Append(" FROM ").Append(source).Append(where).Append(')');
        sql.Append(" SELECT key");

        for (int i = 0; i < request.Metrics.Count; i++)
        {
            sql.Append(", m").Append(i);
        }

        for (int i = 0; i < request.Metrics.Count; i++)
        {
            sql.Append(", (SELECT m").Append(i).Append(" FROM totals) AS t").Append(i);
        }

        sql.Append(", (SELECT count() FROM grouped) AS group_count FROM grouped ORDER BY ");
        sql.Append(request.OrderByKey ? "key" : "m" + request.OrderByMetric.ToString(CultureInfo.InvariantCulture));
        sql.Append(request.Descending ? " DESC" : " ASC");
        sql.Append(", key ASC LIMIT {limit:Int32}");

        return (sql.ToString(), parameters);
    }

    private static string EventTypeParameter(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"metric{index}EventType");

    private static string Condition(int index) =>
        "event_type = {" + EventTypeParameter(index) + ":String}";

    private static string RawExpression(in PivotRequest request, in PivotMetric metric, int index)
    {
        bool scoped = metric.EventType is not null;
        string condition = scoped ? Condition(index) : string.Empty;

        switch (metric.Kind)
        {
            case PivotMetricKind.Events:
                if (request.Exact)
                {
                    return scoped ? $"uniqExactIf(event_id, {condition})" : "uniqExact(event_id)";
                }

                return scoped ? $"countIf({condition})" : "count()";
            case PivotMetricKind.Sessions:
                return Distinct("session_id", request.Approximate, condition);
            case PivotMetricKind.Users:
                return Distinct("user_id", request.Approximate, condition);
            case PivotMetricKind.Distinct:
                return Distinct(metric.Column!, request.Approximate, condition);
            case PivotMetricKind.Measure:
                string suffix = scoped ? "If" : string.Empty;
                string parameters = metric.AggregationParameters is null ? string.Empty : "(" + metric.AggregationParameters + ")";
                string arguments = scoped ? metric.Column + ", " + condition : metric.Column!;
                return metric.AggregationName + suffix + parameters + "(" + arguments + ")";
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric.Kind, "Unhandled metric kind.");
        }
    }

    private static string RollupExpression(in PivotMetric metric, string stateColumn, int index)
    {
        int quantileAt = stateColumn.IndexOf(':', StringComparison.Ordinal);

        if (quantileAt > 0)
        {
            string level = RollupRegistry.QuantileIndex(stateColumn[..quantileAt])!.Value.ToString(CultureInfo.InvariantCulture);
            string column = stateColumn[(quantileAt + 1)..];
            string merged = metric.EventType is null
                ? $"quantilesMerge({RollupRegistry.QuantileLevels})({column})"
                : $"quantilesMergeIf({RollupRegistry.QuantileLevels})({column}, {Condition(index)})";
            return $"arrayElement({merged}, {level})";
        }

        string function = metric.Kind switch
        {
            PivotMetricKind.Events => "count",
            PivotMetricKind.Sessions => "uniqExact",
            PivotMetricKind.Users => "uniqExact",
            PivotMetricKind.Measure => metric.CanonicalAggregation!,
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric.Kind, "Not routable."),
        };

        return metric.EventType is null
            ? $"{function}Merge({stateColumn})"
            : $"{function}MergeIf({stateColumn}, {Condition(index)})";
    }

    private static string Distinct(string column, bool approximate, string condition)
    {
        string function = approximate ? "uniq" : "uniqExact";
        return condition.Length == 0 ? $"{function}({column})" : $"{function}If({column}, {condition})";
    }

    public async Task<PivotResponse> ExecuteAsync(PivotRequest request, CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetPivotQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("nealytics.project_id", request.ProjectId);
        activity?.SetTag("nealytics.tenant_id", request.TenantId);
        activity?.SetTag("nealytics.group_by", request.GroupByColumn);

        LogQueryStarted(_logger, request.ProjectId, request.TenantId, request.GroupByColumn, request.Metrics.Count);
        long startTicks = Stopwatch.GetTimestamp();

        try
        {
            PivotRollupPlan? plan = PivotRollupPlanner.Select(request, _rollups);
            (string sqlCommandText, IReadOnlyList<KeyValuePair<string, object?>> parameters) = BuildQuery(request, plan);

            await using PooledClickHouseConnection lease = await _connectionFactory.AcquireAsync(cancellationToken);
            await using ClickHouseCommand command = lease.Connection.CreateCommand();
            command.CommandText = sqlCommandText;

            foreach (KeyValuePair<string, object?> parameter in parameters)
            {
                command.Parameters.Add(new ClickHouseParameter { ParameterName = parameter.Key, Value = parameter.Value });
            }

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            int metricCount = request.Metrics.Count;
            List<PivotRow> rows = new(request.Limit);
            double[] totals = new double[metricCount];
            long groupCount = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                double[] values = new double[metricCount];

                for (int i = 0; i < metricCount; i++)
                {
                    values[i] = ReadNumber(reader, 1 + i);
                }

                for (int i = 0; i < metricCount; i++)
                {
                    totals[i] = ReadNumber(reader, 1 + metricCount + i);
                }

                groupCount = Convert.ToInt64(reader.GetValue(1 + metricCount * 2), CultureInfo.InvariantCulture);
                rows.Add(new PivotRow { Key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0), Values = values });
            }

            bool truncated = groupCount > rows.Count;
            activity?.SetTag("nealytics.records_returned", rows.Count);
            activity?.SetTag("nealytics.truncated", truncated);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            List<PivotMetricDescriptor> descriptors = new(metricCount);

            foreach (PivotMetric metric in request.Metrics)
            {
                descriptors.Add(new PivotMetricDescriptor { Spec = metric.Wire, Grain = metric.Grain, EventType = metric.EventType });
            }

            return new PivotResponse
            {
                GroupBy = request.GroupByColumn,
                Metrics = descriptors,
                From = request.From,
                To = request.To,
                Source = plan is PivotRollupPlan used ? "rollup:" + used.Rollup.Name : "raw",
                Totals = totals,
                GroupCount = groupCount,
                Truncated = truncated,
                Rows = rows,
            };
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
            TelemetryDiagnostics.QueryReadDuration.Record((double)elapsedTicks / Stopwatch.Frequency);
        }
    }

    private static double ReadNumber(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}

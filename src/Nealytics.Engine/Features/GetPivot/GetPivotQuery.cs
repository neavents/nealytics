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
    private readonly QueryGuard _guard;
    private readonly RollupRegistry _rollups;
    private readonly ILogger<GetPivotQuery> _logger;

    public GetPivotQuery(
        ClickHouseConnectionFactory connectionFactory,
        QueryGuard guard,
        RollupRegistry rollups,
        ILogger<GetPivotQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _guard = guard;
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
                ? PivotAggregates.Rollup(request.Metrics[i], rollup.StateColumns[i], Condition(request.Metrics[i], i))
                : PivotAggregates.Raw(request.Metrics[i], Condition(request.Metrics[i], i), request.Approximate, request.Exact));
            aggregates.Append(" AS m").Append(i);
        }

        StringBuilder where = new(256);
        string source;

        if (plan is PivotRollupPlan routed)
        {
            source = RollupRegistry.Database + "." + routed.Rollup.TableName;
            ScopeClause.AppendRollup(where, request.Scope, "bucket");
        }
        else
        {
            source = RollupRegistry.Database + "." + RollupRegistry.SourceTable;
            ScopeClause.AppendRaw(where, request.Scope);
        }

        StringBuilder sql = new(1024);
        sql.Append("WITH grouped AS (SELECT ");
        sql.Append(plan is null ? "ifNull(toString(" + request.GroupByColumn + "), '')" : request.GroupByColumn);
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

    private static string Condition(in PivotMetric metric, int index) =>
        metric.EventType is null ? string.Empty : "event_type = {" + EventTypeParameter(index) + ":String}";

    public async Task<PivotResponse> ExecuteAsync(PivotRequest request, CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetPivotQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("neavents.project_id", request.ProjectId);
        activity?.SetTag("neavents.tenant_id", request.TenantId);
        activity?.SetTag("nealytics.group_by", request.GroupByColumn);

        LogQueryStarted(_logger, request.ProjectId, request.TenantId, request.GroupByColumn, request.Metrics.Count);
        long startTicks = Stopwatch.GetTimestamp();

        try
        {
            PivotRollupPlan? plan = PivotRollupPlanner.Select(request, _rollups);
            (string sqlCommandText, IReadOnlyList<KeyValuePair<string, object?>> parameters) = BuildQuery(request, plan);

            await using PooledClickHouseConnection lease = await _connectionFactory.AcquireAsync(cancellationToken);
            await using ClickHouseCommand command = lease.Connection.CreateCommand();
            command.CommandText = _guard.Limit(sqlCommandText);

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
            activity?.SetTag("neavents.records_returned", rows.Count);
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

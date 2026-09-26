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
        string?[]? presence = request.EmptyAsNull ? new string?[request.Metrics.Count] : null;

        for (int i = 0; i < request.Metrics.Count; i++)
        {
            if (i > 0)
            {
                aggregates.Append(", ");
            }

            string condition = Condition(request.Metrics[i], i);
            aggregates.Append(plan is PivotRollupPlan rollup
                ? PivotAggregates.Rollup(request.Metrics[i], rollup.StateColumns[i], condition)
                : PivotAggregates.Raw(request.Metrics[i], condition, request.Approximate, request.Exact));
            aggregates.Append(" AS m").Append(i);

            if (presence is not null
                && PivotAggregates.Presence(request.Metrics[i], condition, plan is not null) is string rows)
            {
                aggregates.Append(", ").Append(rows).Append(" AS n").Append(i);
                presence[i] = "n" + i.ToString(CultureInfo.InvariantCulture);
            }
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
            sql.Append(", ");
            AppendCell(sql, request.Metrics[i], i, presence);
        }

        for (int i = 0; i < request.Metrics.Count; i++)
        {
            sql.Append(", (SELECT ");
            AppendCell(sql, request.Metrics[i], i, presence);
            sql.Append(" FROM totals) AS t").Append(i);
        }

        sql.Append(", (SELECT count() FROM grouped) AS group_count FROM grouped ORDER BY ");
        sql.Append(request.OrderByKey ? "key" : "m" + request.OrderByMetric.ToString(CultureInfo.InvariantCulture));
        sql.Append(request.Descending ? " DESC" : " ASC");
        sql.Append(", key ASC LIMIT {limit:Int32}");

        return (sql.ToString(), parameters);
    }

    private static void AppendCell(StringBuilder sql, in PivotMetric metric, int index, string?[]? presence)
    {
        if (presence is null)
        {
            sql.Append('m').Append(index);
            return;
        }

        sql.Append(PivotAggregates.OrNull(metric, "m" + index.ToString(CultureInfo.InvariantCulture), presence[index]));
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
            double? empty = request.EmptyAsNull ? null : 0;
            double?[] totals = new double?[metricCount];
            Array.Fill(totals, empty);
            long groupCount = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                double?[] values = new double?[metricCount];

                for (int i = 0; i < metricCount; i++)
                {
                    values[i] = ReadNumber(reader, 1 + i, empty);
                }

                for (int i = 0; i < metricCount; i++)
                {
                    totals[i] = ReadNumber(reader, 1 + metricCount + i, empty);
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

    private static double? ReadNumber(DbDataReader reader, int ordinal, double? empty) =>
        reader.IsDBNull(ordinal) ? empty : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}

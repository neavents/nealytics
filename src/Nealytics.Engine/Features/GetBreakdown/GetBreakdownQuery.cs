namespace Nealytics.Engine.Features.GetBreakdown;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nealytics.Engine.Infrastructure.Configuration;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class GetBreakdownQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly RollupRegistry _rollups;
    private readonly ILogger<GetBreakdownQuery> _logger;

    public GetBreakdownQuery(
        ClickHouseConnectionFactory connectionFactory,
        RollupRegistry rollups,
        ILogger<GetBreakdownQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _rollups = rollups;
        _logger = logger;
    }

    [LoggerMessage(EventId = 6101, Level = LogLevel.Information,
        Message = "Executing breakdown for Project: {ProjectId} / Tenant: {TenantId} — "
            + "{Metric} by {GroupBy}, {FilterCount} filter(s).")]
    private static partial void LogQueryStarted(
        ILogger logger, string projectId, string tenantId, string metric, string groupBy, int filterCount);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Warning,
        Message = "Breakdown by {GroupBy} truncated: {GroupCount} groups exist, {Limit} returned. "
            + "The response says so — callers must not read the rows as complete.")]
    private static partial void LogTruncated(ILogger logger, string groupBy, long groupCount, int limit);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in BreakdownRequest request) => BuildQuery(request, null);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in BreakdownRequest request, RollupPlan? plan)
    {
        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(request.Scope, 12);
        parameters.Add(new KeyValuePair<string, object?>("limit", request.Limit));
        TenantGrouping.AddParameter(parameters, request.GroupByColumn, plan?.Rollup);

        if (plan is RollupPlan rollup)
        {
            return BuildRollupQuery(request, rollup, parameters);
        }

        string aggregate = request.Metric switch
        {
            BreakdownMetric.Events => request.Exact ? "uniqExact(event_id)" : "count()",
            BreakdownMetric.Sessions => request.Approximate ? "uniq(session_id)" : "uniqExact(session_id)",
            BreakdownMetric.Users => request.Approximate ? "uniq(user_id)" : "uniqExact(user_id)",

            BreakdownMetric.Measure => $"{request.MeasureFunction}({request.MeasureColumn})",

            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Metric, "Unhandled metric."),
        };

        StringBuilder where = new(256);
        ScopeClause.AppendRaw(where, request.Scope);

        if (request.Metric == BreakdownMetric.Users)
        {
            where.Append(" AND user_id IS NOT NULL");
        }

        string orderBy = request.Order switch
        {
            BreakdownOrder.ValueDescending => "value DESC",
            BreakdownOrder.ValueAscending => "value ASC",
            BreakdownOrder.KeyAscending => "key ASC",
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Order, "Unhandled order."),
        };

        StringBuilder sql = new(768);
        sql.Append(TenantGrouping.With(request.GroupByColumn, null));
        sql.Append("grouped AS (SELECT ");
        sql.Append(TenantGrouping.RawKey(request.GroupByColumn));
        sql.Append(" AS key, ");
        sql.Append(aggregate);
        sql.Append(" AS value FROM nealytics_core.global_events");
        sql.Append(where);
        sql.Append(" GROUP BY key)");
        sql.Append(" SELECT key, value,");
        sql.Append(" (SELECT sum(value) FROM grouped) AS grand_total,");
        sql.Append(" (SELECT count() FROM grouped) AS group_count");
        sql.Append(" FROM grouped ORDER BY ");
        sql.Append(orderBy);
        sql.Append(" LIMIT {limit:Int32}");

        return (sql.ToString(), parameters);
    }

    public async Task<BreakdownResponse> ExecuteAsync(
        BreakdownRequest request, CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetBreakdownQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("nealytics.project_id", request.ProjectId);
        activity?.SetTag("nealytics.tenant_id", request.TenantId);
        activity?.SetTag("nealytics.group_by", request.GroupByColumn);

        string metricWire = request.MetricWire ?? BreakdownRequestFactory.ToWireFormat(request.Metric);
        LogQueryStarted(
            _logger, request.ProjectId, request.TenantId, metricWire,
            request.GroupByColumn, request.Filters.Count);

        long startTicks = Stopwatch.GetTimestamp();

        try
        {
            RollupPlan? plan = RollupPlanner.Select(request, _rollups);

            (string sqlCommandText, IReadOnlyList<KeyValuePair<string, object?>> parameters) =
                BuildQuery(request, plan);

            await using PooledClickHouseConnection lease =
                await _connectionFactory.AcquireAsync(cancellationToken);

            await using ClickHouseCommand command = lease.Connection.CreateCommand();
            command.CommandText = sqlCommandText;

            foreach (KeyValuePair<string, object?> parameter in parameters)
            {
                command.Parameters.Add(new ClickHouseParameter
                {
                    ParameterName = parameter.Key,
                    Value = parameter.Value,
                });
            }

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            List<BreakdownRow> rows = new(request.Limit);
            double total = 0;
            long groupCount = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new BreakdownRow
                {
                    Key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    Value = reader.IsDBNull(1)
                        ? 0
                        : Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture),
                });

                total = reader.IsDBNull(2)
                    ? 0
                    : Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture);
                groupCount = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
            }

            if (total > 0)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    rows[i].Share = (double)rows[i].Value / total;
                }
            }

            bool truncated = groupCount > rows.Count;
            if (truncated)
            {
                LogTruncated(_logger, request.GroupByColumn, groupCount, request.Limit);
            }

            activity?.SetTag("nealytics.records_returned", rows.Count);
            activity?.SetTag("nealytics.truncated", truncated);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return new BreakdownResponse
            {
                GroupBy = request.GroupByColumn,
                Metric = metricWire,
                Grain = BreakdownRequestFactory.ToGrain(request.Metric),
                Source = plan is RollupPlan used ? "rollup:" + used.Rollup.Name : "raw",
                From = request.From,
                To = request.To,
                Total = total,
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

    private static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildRollupQuery(
        in BreakdownRequest request,
        RollupPlan plan,
        List<KeyValuePair<string, object?>> parameters)
    {
        StringBuilder where = new(256);
        ScopeClause.AppendRollup(where, request.Scope, "bucket", plan.Rollup);

        string orderBy = request.Order switch
        {
            BreakdownOrder.ValueDescending => "value DESC",
            BreakdownOrder.ValueAscending => "value ASC",
            BreakdownOrder.KeyAscending => "key ASC",
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Order, "Unhandled order."),
        };

        StringBuilder sql = new(768);
        sql.Append(TenantGrouping.With(request.GroupByColumn, plan.Rollup));
        sql.Append("grouped AS (SELECT ");
        sql.Append(TenantGrouping.RollupKey(request.GroupByColumn, plan.Rollup));
        sql.Append(" AS key, ");
        sql.Append(plan.ValueExpression);
        sql.Append(" AS value FROM ");
        sql.Append(RollupRegistry.Database);
        sql.Append('.');
        sql.Append(plan.Rollup.TableName);
        sql.Append(where);
        sql.Append(" GROUP BY key");

        if (plan.DropZeroGroups)
        {
            sql.Append(" HAVING value > 0");
        }

        sql.Append(')');
        sql.Append(" SELECT key, value,");
        sql.Append(" (SELECT sum(value) FROM grouped) AS grand_total,");
        sql.Append(" (SELECT count() FROM grouped) AS group_count");
        sql.Append(" FROM grouped ORDER BY ");
        sql.Append(orderBy);
        sql.Append(" LIMIT {limit:Int32}");

        return (sql.ToString(), parameters);
    }
}

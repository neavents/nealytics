namespace Nealytics.Engine.Features.GetBreakdown;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

/// <summary>
/// The generic breakdown: count a metric, grouped by any allowed column.
///
/// This is the query that makes the engine reusable. It knows nothing about what it is grouping —
/// the column name arrives from <see cref="BreakdownColumns"/>, which is built from the
/// deployment's declared dimensions, so the same code answers "top menus" here and "top authors"
/// in a clone.
/// </summary>
public sealed partial class GetBreakdownQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly ILogger<GetBreakdownQuery> _logger;

    public GetBreakdownQuery(
        ClickHouseConnectionFactory connectionFactory,
        ILogger<GetBreakdownQuery> logger)
    {
        _connectionFactory = connectionFactory;
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

    /// <summary>
    /// Builds the statement.
    ///
    /// Every caller-supplied <i>value</i> is a ClickHouse parameter. The only interpolated text is
    /// <see cref="BreakdownRequest.GroupByColumn"/> and each filter's column, and those are not
    /// caller strings at all — they are instances the allowlist held before the request arrived.
    /// ClickHouse has no parameter form for an identifier, so this is the one safe construction
    /// available, and it is safe because the caller's bytes never reach the builder.
    /// </summary>
    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in BreakdownRequest request)
    {
        List<KeyValuePair<string, object?>> parameters =
        [
            new("projectId", request.ProjectId),
            new("tenantId", request.TenantId),
            new("fromTimestamp", request.From),
            new("toTimestamp", request.To),
            new("limit", request.Limit),
        ];

        string aggregate = request.Metric switch
        {
            BreakdownMetric.Events => "count()",
            BreakdownMetric.Sessions => "uniqExact(session_id)",
            BreakdownMetric.Users => "uniqExact(user_id)",
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Metric, "Unhandled metric."),
        };

        StringBuilder where = new(256);
        where.Append(" WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}");
        where.Append(" AND timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64}");

        if (!string.IsNullOrEmpty(request.EventType))
        {
            where.Append(" AND event_type = {eventType:String}");
            parameters.Add(new KeyValuePair<string, object?>("eventType", request.EventType));
        }

        // Anonymous rows have a NULL user_id and must not be counted as a user. Without this,
        // uniqExact over a column that is mostly NULL reports one phantom "user" per group.
        if (request.Metric == BreakdownMetric.Users)
        {
            where.Append(" AND user_id IS NOT NULL");
        }

        for (int i = 0; i < request.Filters.Count; i++)
        {
            BreakdownFilter filter = request.Filters[i];
            string parameterName = string.Create(
                CultureInfo.InvariantCulture, $"filter{i.ToString(CultureInfo.InvariantCulture)}");

            where.Append(" AND toString(");
            where.Append(filter.Column);
            where.Append(") = {");
            where.Append(parameterName);
            where.Append(":String}");

            parameters.Add(new KeyValuePair<string, object?>(parameterName, filter.Value));
        }

        string orderBy = request.Order switch
        {
            BreakdownOrder.ValueDescending => "value DESC",
            BreakdownOrder.ValueAscending => "value ASC",
            BreakdownOrder.KeyAscending => "key ASC",
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Order, "Unhandled order."),
        };

        // One statement, not three. The rows, the grand total and the number of groups have to
        // agree with each other, and issuing them separately means each sees a different set of
        // rows as ingestion continues — so `share` would not sum to what the totals imply and
        // `truncated` could be computed against a group count the rows never had.
        StringBuilder sql = new(768);
        sql.Append("WITH grouped AS (SELECT ifNull(toString(");
        sql.Append(request.GroupByColumn);
        sql.Append("), '') AS key, ");
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
        activity?.SetTag("neavents.project_id", request.ProjectId);
        activity?.SetTag("neavents.tenant_id", request.TenantId);
        activity?.SetTag("nealytics.group_by", request.GroupByColumn);

        string metricWire = BreakdownRequestFactory.ToWireFormat(request.Metric);
        LogQueryStarted(
            _logger, request.ProjectId, request.TenantId, metricWire,
            request.GroupByColumn, request.Filters.Count);

        long startTicks = Stopwatch.GetTimestamp();

        try
        {
            (string sqlCommandText, IReadOnlyList<KeyValuePair<string, object?>> parameters) =
                BuildQuery(request);

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
            long total = 0;
            long groupCount = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new BreakdownRow
                {
                    Key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    Value = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                });

                total = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                groupCount = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
            }

            // Against the grand total, not the sum of returned rows: a capped response's shares
            // then add up to less than one, which is the honest reading of a partial list.
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

            activity?.SetTag("neavents.records_returned", rows.Count);
            activity?.SetTag("nealytics.truncated", truncated);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return new BreakdownResponse
            {
                GroupBy = request.GroupByColumn,
                Metric = metricWire,
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
}

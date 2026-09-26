namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using System.Globalization;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class GetEventTimeSeriesQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly QueryGuard _guard;
    private readonly ILogger<GetEventTimeSeriesQuery> _logger;

    public GetEventTimeSeriesQuery(
        ClickHouseConnectionFactory connectionFactory,
        QueryGuard guard,
        ILogger<GetEventTimeSeriesQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _guard = guard;
        _logger = logger;
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information,
        Message = "Executing time-series query for Project: {ProjectId} / Tenant: {TenantId} at {Interval} granularity.")]
    private static partial void LogQueryStarted(ILogger logger, string projectId, string tenantId, string interval);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in EventTimeSeriesRequest request)
    {
        string bucketFunction = TimeSeriesIntervalParser.ToBucketFunction(request.Interval);
        bool grouped = !string.IsNullOrEmpty(request.GroupByColumn);

        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(request.Scope, 10);

        if (!string.IsNullOrEmpty(request.TimeZone))
        {
            parameters.Add(new KeyValuePair<string, object?>("tz", request.TimeZone));
        }

        StringBuilder sql = new StringBuilder(256);
        sql.Append("SELECT ");
        sql.Append(bucketFunction);
        sql.Append(TimeBucket.Expression(request.TimeZone));
        sql.Append(" AS bucket, ");

        if (grouped)
        {
            sql.Append(request.GroupByColumn);
            sql.Append(" AS series, ");
        }

        sql.Append("count() AS event_count");

        if (request.Metric is PivotMetric metric)
        {
            string condition = string.Empty;

            if (metric.EventType is not null)
            {
                parameters.Add(new KeyValuePair<string, object?>("metricEventType", metric.EventType));
                condition = "event_type = {metricEventType:String}";
            }

            string value = PivotAggregates.Raw(metric, condition, approximate: false, exact: false);

            if (request.EmptyAsNull)
            {
                value = PivotAggregates.OrNull(metric, value, PivotAggregates.Presence(metric, condition, rollup: false));
            }

            sql.Append(", ").Append(value).Append(" AS value");
        }

        sql.Append(" FROM nealytics_core.global_events");
        ScopeClause.AppendRaw(sql, request.Scope);

        if (grouped)
        {
            sql.Append(" GROUP BY bucket, series ORDER BY bucket ASC, series ASC LIMIT {limit:Int32}");
        }
        else
        {
            sql.Append(" GROUP BY bucket ORDER BY bucket ASC LIMIT {limit:Int32}");
        }

        parameters.Add(new KeyValuePair<string, object?>("limit", request.Limit));

        return (sql.ToString(), parameters);
    }

    public async Task<EventTimeSeriesResponse> ExecuteAsync(
        EventTimeSeriesRequest request,
        CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetEventTimeSeriesQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("neavents.project_id", request.ProjectId);
        activity?.SetTag("neavents.tenant_id", request.TenantId);

        string intervalWire = TimeSeriesIntervalParser.ToWireFormat(request.Interval);
        LogQueryStarted(_logger, request.ProjectId, request.TenantId, intervalWire);
        long startTicks = Stopwatch.GetTimestamp();

        try
        {
            (string sqlCommandText, IReadOnlyList<KeyValuePair<string, object?>> parameters) = BuildQuery(request);

            await using PooledClickHouseConnection lease =
                await _connectionFactory.AcquireAsync(cancellationToken);

            await using ClickHouseCommand command = lease.Connection.CreateCommand();
            command.CommandText = _guard.Limit(sqlCommandText);

            foreach (KeyValuePair<string, object?> parameter in parameters)
            {
                command.Parameters.Add(new ClickHouseParameter
                {
                    ParameterName = parameter.Key,
                    Value = parameter.Value
                });
            }

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            bool grouped = !string.IsNullOrEmpty(request.GroupByColumn);
            int valueOrdinal = grouped ? 3 : 2;
            bool measured = request.Metric is not null;
            List<EventTimeSeriesPoint> points = new List<EventTimeSeriesPoint>(request.Limit);
            long totalCount = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                string? series = null;
                long bucketCount;

                if (grouped)
                {
                    series = reader.IsDBNull(1) ? null : reader.GetString(1);
                    bucketCount = Convert.ToInt64(reader.GetValue(2));
                }
                else
                {
                    bucketCount = Convert.ToInt64(reader.GetValue(1));
                }

                EventTimeSeriesPoint point = new EventTimeSeriesPoint
                {
                    Bucket = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                    Series = series,
                    Count = bucketCount,
                    Value = measured ? ReadValue(reader, valueOrdinal) : null
                };
                points.Add(point);
                totalCount += bucketCount;
            }

            activity?.SetTag("neavents.buckets_returned", points.Count);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return new EventTimeSeriesResponse
            {
                ProjectId = request.ProjectId,
                TenantId = request.TenantId,
                Interval = intervalWire,
                Metric = request.Metric?.Wire,
                From = request.From,
                To = request.To,
                TotalCount = totalCount,
                Points = points
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
            double executionSeconds = (double)elapsedTicks / Stopwatch.Frequency;
            TelemetryDiagnostics.QueryReadDuration.Record(executionSeconds);
        }
    }

    private static double? ReadValue(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        double value = Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        return double.IsFinite(value) ? value : null;
    }
}

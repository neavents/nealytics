namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class GetEventTimeSeriesQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly ILogger<GetEventTimeSeriesQuery> _logger;

    public GetEventTimeSeriesQuery(
        ClickHouseConnectionFactory connectionFactory,
        ILogger<GetEventTimeSeriesQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information,
        Message = "Executing time-series query for Project: {ProjectId} / Tenant: {TenantId} at {Interval} granularity.")]
    private static partial void LogQueryStarted(ILogger logger, string projectId, string tenantId, string interval);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in EventTimeSeriesRequest request)
    {
        string bucketFunction = TimeSeriesIntervalParser.ToBucketFunction(request.Interval);

        List<KeyValuePair<string, object?>> parameters = new List<KeyValuePair<string, object?>>(6)
        {
            new KeyValuePair<string, object?>("projectId", request.ProjectId),
            new KeyValuePair<string, object?>("tenantId", request.TenantId),
            new KeyValuePair<string, object?>("fromTimestamp", request.From),
            new KeyValuePair<string, object?>("toTimestamp", request.To)
        };

        StringBuilder sql = new StringBuilder(256);
        sql.Append("SELECT ");
        sql.Append(bucketFunction);
        sql.Append("(timestamp) AS bucket, count() AS event_count ");
        sql.Append("FROM nealytics_core.global_events ");
        sql.Append("WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String} ");
        sql.Append("AND timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64}");

        if (!string.IsNullOrEmpty(request.EventType))
        {
            sql.Append(" AND event_type = {eventType:String}");
            parameters.Add(new KeyValuePair<string, object?>("eventType", request.EventType));
        }

        sql.Append(" GROUP BY bucket ORDER BY bucket ASC LIMIT {limit:Int32}");
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
            command.CommandText = sqlCommandText;

            foreach (KeyValuePair<string, object?> parameter in parameters)
            {
                command.Parameters.Add(new ClickHouseParameter
                {
                    ParameterName = parameter.Key,
                    Value = parameter.Value
                });
            }

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            List<EventTimeSeriesPoint> points = new List<EventTimeSeriesPoint>(request.Limit);
            long totalCount = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                long bucketCount = Convert.ToInt64(reader.GetValue(1));
                EventTimeSeriesPoint point = new EventTimeSeriesPoint
                {
                    Bucket = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                    Count = bucketCount
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
}

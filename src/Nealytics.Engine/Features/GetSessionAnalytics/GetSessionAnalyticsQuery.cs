namespace Nealytics.Engine.Features.GetSessionAnalytics;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class GetSessionAnalyticsQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly ILogger<GetSessionAnalyticsQuery> _logger;

    private const string SqlCommandText =
        "SELECT session_id, min(timestamp) AS first_seen, max(timestamp) AS last_seen, count() AS event_count " +
        "FROM nealytics_core.global_events " +
        "WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String} " +
        "AND timestamp >= {fromTimestamp:DateTime} AND timestamp <= {toTimestamp:DateTime} " +
        "GROUP BY session_id " +
        "ORDER BY first_seen DESC " +
        "LIMIT {limit:Int32}";

    public GetSessionAnalyticsQuery(
        ClickHouseConnectionFactory connectionFactory,
        ILogger<GetSessionAnalyticsQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "Executing session analytics query for Project: {ProjectId} / Tenant: {TenantId}.")]
    private static partial void LogQueryStarted(ILogger logger, string projectId, string tenantId);

    public async Task<SessionAnalyticsResponse> ExecuteAsync(
        string projectId,
        string tenantId,
        DateTime fromUtc,
        DateTime toUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetSessionAnalyticsQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("neavents.project_id", projectId);
        activity?.SetTag("neavents.tenant_id", tenantId);

        LogQueryStarted(_logger, projectId, tenantId);
        long startTicks = Stopwatch.GetTimestamp();

        try
        {
            await using PooledClickHouseConnection lease =
                await _connectionFactory.AcquireAsync(cancellationToken);

            await using ClickHouseCommand command = lease.Connection.CreateCommand();
            command.CommandText = SqlCommandText;

            command.Parameters.Add(new ClickHouseParameter { ParameterName = "projectId", Value = projectId });
            command.Parameters.Add(new ClickHouseParameter { ParameterName = "tenantId", Value = tenantId });
            command.Parameters.Add(new ClickHouseParameter { ParameterName = "fromTimestamp", Value = fromUtc });
            command.Parameters.Add(new ClickHouseParameter { ParameterName = "toTimestamp", Value = toUtc });
            command.Parameters.Add(new ClickHouseParameter { ParameterName = "limit", Value = limit });

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            List<SessionSummaryItem> sessions = new List<SessionSummaryItem>(limit);
            long totalEventCount = 0;
            double totalDurationSeconds = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                DateTime firstSeen = reader.GetDateTime(1);
                DateTime lastSeen = reader.GetDateTime(2);
                double durationSeconds = (lastSeen - firstSeen).TotalSeconds;
                int eventCount = Convert.ToInt32(reader.GetValue(3));

                SessionSummaryItem item = new SessionSummaryItem
                {
                    SessionId = reader.GetString(0),
                    FirstSeen = firstSeen,
                    LastSeen = lastSeen,
                    DurationSeconds = durationSeconds,
                    EventCount = eventCount
                };

                sessions.Add(item);
                totalEventCount += eventCount;
                totalDurationSeconds += durationSeconds;
            }

            int uniqueSessionCount = sessions.Count;
            double avgDurationSeconds = uniqueSessionCount > 0
                ? totalDurationSeconds / uniqueSessionCount
                : 0;

            activity?.SetTag("neavents.sessions_returned", uniqueSessionCount);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return new SessionAnalyticsResponse
            {
                ProjectId = projectId,
                TenantId = tenantId,
                UniqueSessionCount = uniqueSessionCount,
                TotalEventCount = totalEventCount,
                AvgDurationSeconds = avgDurationSeconds,
                Sessions = sessions
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

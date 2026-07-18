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
        "AND timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64} " +
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

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in SessionAnalyticsRequest request)
    {
        List<KeyValuePair<string, object?>> parameters = new List<KeyValuePair<string, object?>>(5)
        {
            new KeyValuePair<string, object?>("projectId", request.ProjectId),
            new KeyValuePair<string, object?>("tenantId", request.TenantId),
            new KeyValuePair<string, object?>("fromTimestamp", request.From),
            new KeyValuePair<string, object?>("toTimestamp", request.To),
            new KeyValuePair<string, object?>("limit", request.Limit)
        };

        return (SqlCommandText, parameters);
    }

    internal static SessionAnalyticsResponse Aggregate(
        string projectId,
        string tenantId,
        IReadOnlyList<SessionSummaryItem> sessions)
    {
        long totalEventCount = 0;
        double totalDurationSeconds = 0;

        for (int i = 0; i < sessions.Count; i++)
        {
            totalEventCount += sessions[i].EventCount;
            totalDurationSeconds += sessions[i].DurationSeconds;
        }

        int uniqueSessionCount = sessions.Count;
        double avgDurationSeconds = uniqueSessionCount > 0
            ? totalDurationSeconds / uniqueSessionCount
            : 0;

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

    public async Task<SessionAnalyticsResponse> ExecuteAsync(
        SessionAnalyticsRequest request,
        CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetSessionAnalyticsQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("neavents.project_id", request.ProjectId);
        activity?.SetTag("neavents.tenant_id", request.TenantId);

        LogQueryStarted(_logger, request.ProjectId, request.TenantId);
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

            List<SessionSummaryItem> sessions = new List<SessionSummaryItem>(request.Limit);

            while (await reader.ReadAsync(cancellationToken))
            {
                DateTime firstSeen = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
                DateTime lastSeen = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
                double durationSeconds = (lastSeen - firstSeen).TotalSeconds;
                long eventCount = Convert.ToInt64(reader.GetValue(3));

                SessionSummaryItem item = new SessionSummaryItem
                {
                    SessionId = reader.GetString(0),
                    FirstSeen = firstSeen,
                    LastSeen = lastSeen,
                    DurationSeconds = durationSeconds,
                    EventCount = eventCount
                };

                sessions.Add(item);
            }

            activity?.SetTag("neavents.sessions_returned", sessions.Count);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return Aggregate(request.ProjectId, request.TenantId, sessions);
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

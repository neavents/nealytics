namespace Nealytics.Engine.Features.GetProjectTimeline;

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

public sealed partial class GetProjectTimelineQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly ILogger<GetProjectTimelineQuery> _logger;

    private const string SqlCommandText =
        "SELECT event_id, session_id, event_type, item_id, metadata_json, timestamp " +
        "FROM nealytics_core.global_events " +
        "WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String} " +
        "ORDER BY timestamp DESC " +
        "LIMIT {limit:Int32}";

    private const string SqlCommandTextWithCursor =
        "SELECT event_id, session_id, event_type, item_id, metadata_json, timestamp " +
        "FROM nealytics_core.global_events " +
        "WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String} " +
        "AND timestamp < {cursor:DateTime} " +
        "ORDER BY timestamp DESC " +
        "LIMIT {limit:Int32}";

    public GetProjectTimelineQuery(
        ClickHouseConnectionFactory connectionFactory,
        ILogger<GetProjectTimelineQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "Executing timeline query for Project: {ProjectId} / Tenant: {TenantId}.")]
    private static partial void LogQueryStarted(ILogger logger, string projectId, string tenantId);

    public async Task<ProjectTimelineResponse> ExecuteAsync(
        string projectId,
        string tenantId,
        int limit,
        DateTime? cursor,
        CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetProjectTimelineQuery.Execute");
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
            command.CommandText = cursor.HasValue ? SqlCommandTextWithCursor : SqlCommandText;

            command.Parameters.Add(new ClickHouseParameter { ParameterName = "projectId", Value = projectId });
            command.Parameters.Add(new ClickHouseParameter { ParameterName = "tenantId", Value = tenantId });
            command.Parameters.Add(new ClickHouseParameter { ParameterName = "limit", Value = limit });

            if (cursor.HasValue)
            {
                command.Parameters.Add(new ClickHouseParameter { ParameterName = "cursor", Value = cursor.Value });
            }

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            List<GlobalTimelineItem> events = new List<GlobalTimelineItem>(limit);

            while (await reader.ReadAsync(cancellationToken))
            {
                GlobalTimelineItem item = new GlobalTimelineItem
                {
                    EventId = reader.GetGuid(0),
                    SessionId = reader.GetString(1),
                    EventType = reader.GetString(2),
                    ItemId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    MetadataJson = reader.GetString(4),
                    Timestamp = DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)
                };
                events.Add(item);
            }

            activity?.SetTag("neavents.records_returned", events.Count);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return new ProjectTimelineResponse
            {
                ProjectId = projectId,
                TenantId = tenantId,
                Events = events
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

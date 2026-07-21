namespace Nealytics.Engine.Features.GetProjectTimeline;

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

public sealed partial class GetProjectTimelineQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly ILogger<GetProjectTimelineQuery> _logger;

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

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in TimelineQueryRequest request)
    {
        List<KeyValuePair<string, object?>> parameters = new List<KeyValuePair<string, object?>>(7)
        {
            new KeyValuePair<string, object?>("projectId", request.ProjectId),
            new KeyValuePair<string, object?>("tenantId", request.TenantId)
        };

        StringBuilder sql = new StringBuilder(
            "SELECT event_id, session_id, user_id, event_type, item_id, metadata_json, timestamp " +
            "FROM nealytics_core.global_events " +
            "WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}");

        if (request.Before.HasValue)
        {
            sql.Append(" AND timestamp < {cursor:DateTime64}");
            parameters.Add(new KeyValuePair<string, object?>("cursor", request.Before.Value));
        }

        if (!string.IsNullOrEmpty(request.EventType))
        {
            sql.Append(" AND event_type = {eventType:String}");
            parameters.Add(new KeyValuePair<string, object?>("eventType", request.EventType));
        }

        if (!string.IsNullOrEmpty(request.SessionId))
        {
            sql.Append(" AND session_id = {sessionId:String}");
            parameters.Add(new KeyValuePair<string, object?>("sessionId", request.SessionId));
        }

        if (!string.IsNullOrEmpty(request.ItemId))
        {
            sql.Append(" AND item_id = {itemId:String}");
            parameters.Add(new KeyValuePair<string, object?>("itemId", request.ItemId));
        }

        if (!string.IsNullOrEmpty(request.MetaKey) && !string.IsNullOrEmpty(request.MetaValue))
        {
            sql.Append(" AND JSONExtractString(metadata_json, {metaKey:String}) = {metaValue:String}");
            parameters.Add(new KeyValuePair<string, object?>("metaKey", request.MetaKey));
            parameters.Add(new KeyValuePair<string, object?>("metaValue", request.MetaValue));
        }

        sql.Append(" ORDER BY timestamp DESC LIMIT {limit:Int32}");
        parameters.Add(new KeyValuePair<string, object?>("limit", request.Limit));

        return (sql.ToString(), parameters);
    }

    public async Task<ProjectTimelineResponse> ExecuteAsync(
        TimelineQueryRequest request,
        CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetProjectTimelineQuery.Execute");
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

            List<GlobalTimelineItem> events = new List<GlobalTimelineItem>(request.Limit);

            while (await reader.ReadAsync(cancellationToken))
            {
                GlobalTimelineItem item = new GlobalTimelineItem
                {
                    EventId = reader.GetGuid(0),
                    SessionId = reader.GetString(1),
                    UserId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    EventType = reader.GetString(3),
                    ItemId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    MetadataJson = reader.GetString(5),
                    Timestamp = DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)
                };
                events.Add(item);
            }

            activity?.SetTag("neavents.records_returned", events.Count);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return new ProjectTimelineResponse
            {
                ProjectId = request.ProjectId,
                TenantId = request.TenantId,
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

namespace Nealytics.Engine.Features.GetProjectTimeline;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Configuration;
using System.Linq;
using System.Globalization;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class GetProjectTimelineQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly QueryGuard _guard;
    private readonly ILogger<GetProjectTimelineQuery> _logger;
    private readonly string[] _dimensionColumns;
    private readonly string[] _measureColumns;
    private readonly string[] _declaredColumns;

    public GetProjectTimelineQuery(
        ClickHouseConnectionFactory connectionFactory,
        QueryGuard guard,
        DimensionRegistry dimensions,
        MeasureRegistry measures,
        ILogger<GetProjectTimelineQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _guard = guard;
        _logger = logger;
        _dimensionColumns = [.. dimensions.Active.Select(d => d.Name)];
        _measureColumns = [.. measures.Active.Select(m => m.Name)];
        _declaredColumns = [.. _dimensionColumns, .. _measureColumns];
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "Executing timeline query for Project: {ProjectId} / Tenant: {TenantId}.")]
    private static partial void LogQueryStarted(ILogger logger, string projectId, string tenantId);

    internal const int CoreColumnCount = 7;

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in TimelineQueryRequest request,
        IReadOnlyList<string>? declaredColumns = null)
    {
        List<KeyValuePair<string, object?>> parameters = new List<KeyValuePair<string, object?>>(10)
        {
            new KeyValuePair<string, object?>("projectId", request.ProjectId),
            new KeyValuePair<string, object?>("tenantId", request.TenantId),
            new KeyValuePair<string, object?>("notBefore", request.NotBefore)
        };

        StringBuilder sql = new StringBuilder(
            "SELECT event_id, session_id, user_id, event_type, object_id, metadata_json, timestamp");

        if (declaredColumns is not null)
        {
            for (int i = 0; i < declaredColumns.Count; i++)
            {
                sql.Append(", ");
                sql.Append(declaredColumns[i]);
            }
        }

        sql.Append(
            " FROM nealytics_core.global_events " +
            "WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String} " +
            "AND timestamp >= {notBefore:DateTime64}");

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

        if (!string.IsNullOrEmpty(request.ObjectId))
        {
            sql.Append(" AND object_id = {objectId:String}");
            parameters.Add(new KeyValuePair<string, object?>("objectId", request.ObjectId));
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
            (string sqlCommandText, IReadOnlyList<KeyValuePair<string, object?>> parameters) = BuildQuery(request, _declaredColumns);

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

            List<GlobalTimelineItem> events = new List<GlobalTimelineItem>(request.Limit);

            while (await reader.ReadAsync(cancellationToken))
            {
                GlobalTimelineItem item = new GlobalTimelineItem
                {
                    EventId = reader.GetGuid(0),
                    SessionId = reader.GetString(1),
                    UserId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    EventType = reader.GetString(3),
                    ObjectId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    MetadataJson = reader.GetString(5),
                    Timestamp = DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
                    Dimensions = ReadDeclared(reader, CoreColumnCount, _dimensionColumns),
                    Measures = ReadDeclared(
                        reader, CoreColumnCount + _dimensionColumns.Length, _measureColumns)
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

    private static Dictionary<string, string>? ReadDeclared(
        DbDataReader reader, int offset, string[] columns)
    {
        Dictionary<string, string>? values = null;

        for (int i = 0; i < columns.Length; i++)
        {
            int ordinal = offset + i;

            if (reader.IsDBNull(ordinal))
            {
                continue;
            }

            string? text = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            (values ??= new Dictionary<string, string>(StringComparer.Ordinal))[columns[i]] = text;
        }

        return values;
    }
}

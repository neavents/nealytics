namespace Nealytics.Engine.Features.GetSessionAnalytics;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class GetSessionAnalyticsQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly RollupRegistry _rollups;
    private readonly ILogger<GetSessionAnalyticsQuery> _logger;

    public GetSessionAnalyticsQuery(
        ClickHouseConnectionFactory connectionFactory,
        RollupRegistry rollups,
        ILogger<GetSessionAnalyticsQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _rollups = rollups;
        _logger = logger;
    }

    internal static Rollup? SelectRollup(in SessionAnalyticsRequest request, RollupRegistry rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        if (request.From >= request.To
            || request.From.TimeOfDay != TimeSpan.Zero
            || request.To.TimeOfDay != TimeSpan.Zero
            || ScopeClause.HasMeasureFilter(request.Filters))
        {
            return null;
        }

        foreach (Rollup candidate in rollups.Routable)
        {
            if (candidate.Grain != RollupGrain.Session || candidate.EventTypes.Count != 0)
            {
                continue;
            }

            bool covered = true;

            foreach (QueryFilter filter in request.Filters ?? [])
            {
                if (!candidate.CoversColumn(filter.Column))
                {
                    covered = false;
                    break;
                }
            }

            if (covered)
            {
                return candidate;
            }
        }

        return null;
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "Executing session analytics query for Project: {ProjectId} / Tenant: {TenantId}.")]
    private static partial void LogQueryStarted(ILogger logger, string projectId, string tenantId);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in SessionAnalyticsRequest request) => BuildQuery(request, null);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in SessionAnalyticsRequest request, Rollup? rollup)
    {
        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(request.Scope, 10);
        parameters.Add(new KeyValuePair<string, object?>("limit", request.Limit));

        StringBuilder sql = new(640);
        sql.Append("WITH sessions AS (SELECT session_id, ");

        if (rollup is null)
        {
            sql.Append("min(timestamp) AS first_seen, max(timestamp) AS last_seen, count() AS event_count");
            sql.Append(" FROM nealytics_core.global_events");
            ScopeClause.AppendRaw(sql, request.Scope);
        }
        else
        {
            sql.Append("minMerge(started_at) AS first_seen, maxMerge(ended_at) AS last_seen, countMerge(events) AS event_count");
            sql.Append(" FROM ").Append(RollupRegistry.Database).Append('.').Append(rollup.TableName);
            ScopeClause.AppendProjectAndTenant(sql, request.TenantId, request.TenantSet);
            sql.Append(" AND event_date >= toDate({fromTimestamp:DateTime64}) AND event_date < toDate({toTimestamp:DateTime64})");
            sql.Append(TrafficFilter.Clause(request.TrafficClass));
            AppendRollupFilters(sql, request.Filters);
        }

        sql.Append(" GROUP BY session_id)");
        sql.Append(" SELECT session_id, first_seen, last_seen, event_count,");
        sql.Append(" (SELECT count() FROM sessions) AS total_sessions,");
        sql.Append(" (SELECT sum(event_count) FROM sessions) AS total_events,");
        sql.Append(" (SELECT avg(dateDiff('millisecond', first_seen, last_seen) / 1000) FROM sessions) AS avg_duration");
        sql.Append(" FROM sessions ORDER BY first_seen DESC LIMIT {limit:Int32}");

        return (sql.ToString(), parameters);
    }

    private static void AppendRollupFilters(StringBuilder sql, IReadOnlyList<QueryFilter>? filters)
    {
        if (filters is null)
        {
            return;
        }

        for (int i = 0; i < filters.Count; i++)
        {
            sql.Append(" AND ").Append(filters[i].Column).Append(" = {filter").Append(i).Append(":String}");
        }
    }

    internal static SessionAnalyticsResponse Aggregate(
        string projectId,
        string tenantId,
        IReadOnlyList<SessionSummaryItem> sessions,
        long totalSessions,
        long totalEventCount,
        double avgDurationSeconds,
        int limit,
        string source = "raw")
    {
        return new SessionAnalyticsResponse
        {
            Source = source,
            ProjectId = projectId,
            TenantId = tenantId,
            UniqueSessionCount = totalSessions,
            TotalEventCount = totalEventCount,
            AvgDurationSeconds = avgDurationSeconds,
            Truncated = totalSessions > sessions.Count,
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
        activity?.SetTag("nealytics.project_id", request.ProjectId);
        activity?.SetTag("nealytics.tenant_id", request.TenantId);

        LogQueryStarted(_logger, request.ProjectId, request.TenantId);
        long startTicks = Stopwatch.GetTimestamp();

        try
        {
            Rollup? rollup = SelectRollup(request, _rollups);
            (string sqlCommandText, IReadOnlyList<KeyValuePair<string, object?>> parameters) =
                BuildQuery(request, rollup);
            activity?.SetTag("nealytics.source", rollup is null ? "raw" : "rollup:" + rollup.Name);

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
            long totalSessions = 0;
            long totalEventCount = 0;
            double avgDurationSeconds = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                DateTime firstSeen = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
                DateTime lastSeen = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
                double durationSeconds = (lastSeen - firstSeen).TotalSeconds;
                long eventCount = Convert.ToInt64(reader.GetValue(3));

                totalSessions = Convert.ToInt64(reader.GetValue(4));
                totalEventCount = Convert.ToInt64(reader.GetValue(5));
                avgDurationSeconds = reader.IsDBNull(6)
                    ? 0
                    : Convert.ToDouble(reader.GetValue(6), System.Globalization.CultureInfo.InvariantCulture);

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

            activity?.SetTag("nealytics.sessions_returned", sessions.Count);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return Aggregate(
                request.ProjectId,
                request.TenantId,
                sessions,
                totalSessions,
                totalEventCount,
                avgDurationSeconds,
                request.Limit,
                rollup is null ? "raw" : "rollup:" + rollup.Name);
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

namespace Nealytics.Engine.Features.GetSessionAnalytics;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class GetSessionAnalyticsQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly RollupRegistry _rollups;
    private readonly ILogger<GetSessionAnalyticsQuery> _logger;

    // One statement, not two, and the totals are computed over the whole range rather than the
    // page.
    //
    // This used to return the LIMITed rows and sum them in C#, so uniqueSessionCount was capped by
    // limit — with the default of 100 it read exactly 100 for any tenant with more traffic, and the
    // average duration was the average of whichever 100 sessions happened to sort first. The
    // dashboard's ROW_CAP workaround exists solely because of that. The totals now come from the
    // same statement as the rows, for the reason /breakdown does it: issued separately, each would
    // see a different set of rows as ingestion continues.
    private const string SqlCommandText =
        "WITH sessions AS (" +
        "SELECT session_id, min(timestamp) AS first_seen, max(timestamp) AS last_seen, count() AS event_count " +
        "FROM nealytics_core.global_events " +
        "WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String} " +
        "AND timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64} " +
        "GROUP BY session_id) " +
        "SELECT session_id, first_seen, last_seen, event_count, " +
        "(SELECT count() FROM sessions) AS total_sessions, " +
        "(SELECT sum(event_count) FROM sessions) AS total_events, " +
        "(SELECT avg(dateDiff('millisecond', first_seen, last_seen) / 1000) FROM sessions) AS avg_duration " +
        "FROM sessions " +
        "ORDER BY first_seen DESC " +
        "LIMIT {limit:Int32}";

    public GetSessionAnalyticsQuery(
        ClickHouseConnectionFactory connectionFactory,
        RollupRegistry rollups,
        ILogger<GetSessionAnalyticsQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _rollups = rollups;
        _logger = logger;
    }

    /// <summary>
    /// The session rollup that may answer this request, or null to scan raw.
    ///
    /// Three conditions, and each one is a way the answer would otherwise be quietly wrong rather
    /// than merely slow:
    ///
    /// <list type="bullet">
    /// <item><b>Both ends on midnight.</b> A rollup row covers a whole day, so answering 12:00-18:00
    /// from it would return whole days — a number that is wrong and looks completely healthy.</item>
    /// <item><b>The rollup filters no event types.</b> One declared over <c>app_open,menu_view</c>
    /// holds only those rows, so its per-session event count is not the session's event count. It is
    /// a perfectly good rollup that answers a different question.</item>
    /// <item><b>It exists.</b> Absent one, raw is not a fallback, it is the answer.</item>
    /// </list>
    /// </summary>
    internal static Rollup? SelectRollup(in SessionAnalyticsRequest request, RollupRegistry rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        if (request.From >= request.To
            || request.From.TimeOfDay != TimeSpan.Zero
            || request.To.TimeOfDay != TimeSpan.Zero)
        {
            return null;
        }

        foreach (Rollup candidate in rollups.Declared)
        {
            if (candidate.Grain == RollupGrain.Session && candidate.EventTypes.Count == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The same statement shape read from merged state, so every number the response carries keeps
    /// the meaning it has on the raw path.
    ///
    /// <c>GROUP BY session_id</c> without the date is what recombines a session that crossed
    /// midnight: it is stored as one row per day, and <c>minMerge</c>/<c>maxMerge</c> put the two
    /// halves back together. Verified on 26.7.1 — a 23:50-00:05 session reads back as a single
    /// 900-second session, and an event inserted after the fact extends it with no job to re-run.
    ///
    /// The range is half open on <c>event_date</c>, matching the rollup path in /breakdown, and
    /// <see cref="SelectRollup"/> has already refused any range whose ends are not on midnight.
    /// </summary>
    internal static string BuildRollupSql(Rollup rollup) =>
        "WITH sessions AS (" +
        "SELECT session_id, minMerge(started_at) AS first_seen, maxMerge(ended_at) AS last_seen, " +
        "countMerge(events) AS event_count " +
        "FROM " + RollupRegistry.Database + "." + rollup.TableName + " " +
        "WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String} " +
        "AND event_date >= toDate({fromTimestamp:DateTime64}) AND event_date < toDate({toTimestamp:DateTime64}) " +
        "GROUP BY session_id) " +
        "SELECT session_id, first_seen, last_seen, event_count, " +
        "(SELECT count() FROM sessions) AS total_sessions, " +
        "(SELECT sum(event_count) FROM sessions) AS total_events, " +
        "(SELECT avg(dateDiff('millisecond', first_seen, last_seen) / 1000) FROM sessions) AS avg_duration " +
        "FROM sessions " +
        "ORDER BY first_seen DESC " +
        "LIMIT {limit:Int32}";

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "Executing session analytics query for Project: {ProjectId} / Tenant: {TenantId}.")]
    private static partial void LogQueryStarted(ILogger logger, string projectId, string tenantId);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in SessionAnalyticsRequest request) => BuildQuery(request, null);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in SessionAnalyticsRequest request, Rollup? rollup)
    {
        List<KeyValuePair<string, object?>> parameters = new List<KeyValuePair<string, object?>>(5)
        {
            new KeyValuePair<string, object?>("projectId", request.ProjectId),
            new KeyValuePair<string, object?>("tenantId", request.TenantId),
            new KeyValuePair<string, object?>("fromTimestamp", request.From),
            new KeyValuePair<string, object?>("toTimestamp", request.To),
            new KeyValuePair<string, object?>("limit", request.Limit)
        };

        return (rollup is null ? SqlCommandText : BuildRollupSql(rollup), parameters);
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
        activity?.SetTag("neavents.project_id", request.ProjectId);
        activity?.SetTag("neavents.tenant_id", request.TenantId);

        LogQueryStarted(_logger, request.ProjectId, request.TenantId);
        long startTicks = Stopwatch.GetTimestamp();

        try
        {
            Rollup? rollup = SelectRollup(request, _rollups);
            (string sqlCommandText, IReadOnlyList<KeyValuePair<string, object?>> parameters) =
                BuildQuery(request, rollup);
            activity?.SetTag("neavents.source", rollup is null ? "raw" : "rollup:" + rollup.Name);

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

            activity?.SetTag("neavents.sessions_returned", sessions.Count);
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

namespace Nealytics.Engine.Features.GetFunnel;

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

public sealed partial class GetFunnelQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly ILogger<GetFunnelQuery> _logger;

    public GetFunnelQuery(
        ClickHouseConnectionFactory connectionFactory,
        ILogger<GetFunnelQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Information,
        Message = "Executing funnel for Project: {ProjectId} / Tenant: {TenantId} — {StepCount} step(s), "
            + "{WindowSeconds}s window.")]
    private static partial void LogQueryStarted(
        ILogger logger, string projectId, string tenantId, int stepCount, int windowSeconds);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning,
        Message = "Funnel segmented by '{Column}' has more groups than the limit of {Limit}; the "
            + "response is truncated and its segments are the largest ones only.")]
    private static partial void LogTruncated(ILogger logger, string column, int limit);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in FunnelRequest request)
    {
        List<KeyValuePair<string, object?>> parameters =
        [
            new KeyValuePair<string, object?>("projectId", request.ProjectId),
            new KeyValuePair<string, object?>("tenantId", request.TenantId),
            new KeyValuePair<string, object?>("fromTimestamp", request.From),
            new KeyValuePair<string, object?>("toTimestamp", request.To),
            new KeyValuePair<string, object?>("limit", request.Limit),
        ];

        string unit = request.Grain == FunnelGrain.Users ? "user_id" : "session_id";

        StringBuilder conditions = new(256);
        StringBuilder eventTypeFilter = new(128);

        for (int i = 0; i < request.Steps.Count; i++)
        {
            FunnelStep step = request.Steps[i];
            string typeParam = $"step{i}Type";

            parameters.Add(new KeyValuePair<string, object?>(typeParam, step.EventType));

            conditions.Append(", event_type = {").Append(typeParam).Append(":String}");

            if (step.FilterColumn is not null)
            {
                string valueParam = $"step{i}Value";
                parameters.Add(new KeyValuePair<string, object?>(valueParam, step.FilterValue));

                conditions.Append(" AND toString(").Append(step.FilterColumn)
                    .Append(") = {").Append(valueParam).Append(":String}");
            }

            eventTypeFilter.Append(i == 0 ? " AND event_type IN ({" : ", {")
                .Append(typeParam)
                .Append(":String}");
        }

        eventTypeFilter.Append(')');

        StringBuilder sql = new(768);
        sql.Append("WITH levels AS (SELECT ");

        if (request.BreakdownColumn is not null)
        {
            sql.Append("ifNull(toString(").Append(request.BreakdownColumn).Append("), '') AS segment, ");
        }

        sql.Append("windowFunnel(").Append(request.WindowSeconds.ToString(CultureInfo.InvariantCulture));
        sql.Append(")(toDateTime(timestamp)").Append(conditions).Append(") AS level ");
        sql.Append("FROM nealytics_core.global_events ");
        sql.Append("WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String} ");
        sql.Append("AND timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64}");
        sql.Append(eventTypeFilter);

        if (request.Grain == FunnelGrain.Users)
        {
            sql.Append(" AND user_id IS NOT NULL");
        }

        sql.Append(" GROUP BY ");

        if (request.BreakdownColumn is not null)
        {
            sql.Append("segment, ");
        }

        sql.Append(unit).Append(") SELECT ");

        if (request.BreakdownColumn is not null)
        {
            sql.Append("segment, ");
        }
        else
        {
            sql.Append("'' AS segment, ");
        }

        for (int i = 0; i < request.Steps.Count; i++)
        {
            sql.Append("countIf(level >= ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(") AS s")
                .Append(i.ToString(CultureInfo.InvariantCulture));

            if (i < request.Steps.Count - 1)
            {
                sql.Append(", ");
            }
        }

        sql.Append(" FROM levels");

        if (request.BreakdownColumn is not null)
        {
            sql.Append(" GROUP BY segment ORDER BY s0 DESC LIMIT {limit:Int32}");
        }

        return (sql.ToString(), parameters);
    }

    public async Task<FunnelResponse> ExecuteAsync(
        FunnelRequest request, CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetFunnelQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("neavents.project_id", request.ProjectId);
        activity?.SetTag("neavents.tenant_id", request.TenantId);

        LogQueryStarted(
            _logger, request.ProjectId, request.TenantId, request.Steps.Count, request.WindowSeconds);

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

            List<FunnelSegment> segments = [];
            IReadOnlyList<FunnelStepResult> overall = [];

            while (await reader.ReadAsync(cancellationToken))
            {
                string segmentKey = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);

                long[] counts = new long[request.Steps.Count];

                for (int i = 0; i < counts.Length; i++)
                {
                    counts[i] = Convert.ToInt64(reader.GetValue(i + 1), CultureInfo.InvariantCulture);
                }

                IReadOnlyList<FunnelStepResult> steps = Project(request, counts);

                if (request.BreakdownColumn is null)
                {
                    overall = steps;
                }
                else
                {
                    segments.Add(new FunnelSegment { Key = segmentKey, Steps = steps });
                }
            }

            bool truncated = request.BreakdownColumn is not null && segments.Count >= request.Limit;

            if (truncated)
            {
                LogTruncated(_logger, request.BreakdownColumn!, request.Limit);
            }

            activity?.SetTag("nealytics.truncated", truncated);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return new FunnelResponse
            {
                Grain = request.Grain == FunnelGrain.Users ? "user" : "session",
                BreakdownBy = request.BreakdownColumn,
                WindowSeconds = request.WindowSeconds,
                From = request.From,
                To = request.To,
                Truncated = truncated,
                Steps = overall,
                Segments = segments,
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

    private static IReadOnlyList<FunnelStepResult> Project(in FunnelRequest request, long[] counts)
    {
        List<FunnelStepResult> steps = new(counts.Length);
        long first = counts.Length > 0 ? counts[0] : 0;

        for (int i = 0; i < counts.Length; i++)
        {
            long previous = i == 0 ? counts[i] : counts[i - 1];

            steps.Add(new FunnelStepResult
            {
                Step = i + 1,
                Label = request.Steps[i].EventType,
                Count = counts[i],
                Conversion = first > 0 ? (double)counts[i] / first : 0,
                StepConversion = previous > 0 ? (double)counts[i] / previous : 0,
            });
        }

        return steps;
    }
}

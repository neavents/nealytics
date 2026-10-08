namespace Nealytics.Engine.Features.GetRetention;

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
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class GetRetentionQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly ILogger<GetRetentionQuery> _logger;

    public GetRetentionQuery(ClickHouseConnectionFactory connectionFactory, ILogger<GetRetentionQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    [LoggerMessage(EventId = 6401, Level = LogLevel.Information,
        Message = "Executing retention for Project: {ProjectId} / Tenant: {TenantId} by {Actor} per {Period}.")]
    private static partial void LogQueryStarted(
        ILogger logger, string projectId, string tenantId, string actor, string period);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in RetentionRequest request)
    {
        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(request.Scope, 12);

        StringBuilder where = new(256);
        ScopeClause.AppendRaw(where, request.Scope);

        if (request.Actor == RetentionActor.Users)
        {
            where.Append(" AND user_id IS NOT NULL");
        }

        string actor = request.Actor switch
        {
            RetentionActor.Users => "assumeNotNull(user_id)",
            RetentionActor.Identities => IdentityStitching.StitchedColumn,
            _ => "session_id",
        };

        string period = request.Period switch
        {
            RetentionPeriod.Day => "toDate(timestamp)",
            RetentionPeriod.Month => "toStartOfMonth(timestamp)",
            _ => "toMonday(timestamp)",
        };

        string offset = request.Period switch
        {
            RetentionPeriod.Day => "dateDiff('day', cohorts.cohort, activity.period)",
            RetentionPeriod.Month => "dateDiff('month', cohorts.cohort, activity.period)",
            _ => "intDiv(dateDiff('day', cohorts.cohort, activity.period), 7)",
        };

        StringBuilder sql = new(1024);
        sql.Append("WITH activity AS (SELECT ").Append(actor).Append(" AS actor, ").Append(period).Append(" AS period FROM ");

        if (request.Actor == RetentionActor.Identities)
        {
            IdentityStitching.AddParameter(parameters, request.AliasEventType!);
            IdentityStitching.AppendStitchedSource(sql, request.TenantId, request.TenantSet, where.ToString());
        }
        else
        {
            sql.Append("nealytics_core.global_events").Append(where);
        }

        sql.Append(" GROUP BY actor, period),");
        sql.Append(" cohorts AS (SELECT actor, min(period) AS cohort FROM activity GROUP BY actor)");
        sql.Append(" SELECT toDateTime(cohorts.cohort, 'UTC') AS cohort_start, ").Append(offset).Append(" AS period_offset,");
        sql.Append(" count() AS actors FROM activity INNER JOIN cohorts ON activity.actor = cohorts.actor");
        sql.Append(" GROUP BY cohort_start, period_offset ORDER BY cohort_start, period_offset");

        return (sql.ToString(), parameters);
    }

    public async Task<RetentionResponse> ExecuteAsync(RetentionRequest request, CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetRetentionQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("nealytics.project_id", request.ProjectId);
        activity?.SetTag("nealytics.tenant_id", request.TenantId);

        string by = RetentionRequestFactory.Wire(request.Actor);
        string periodWire = RetentionRequestFactory.Wire(request.Period);
        LogQueryStarted(_logger, request.ProjectId, request.TenantId, by, periodWire);
        long startTicks = Stopwatch.GetTimestamp();

        try
        {
            (string sqlCommandText, IReadOnlyList<KeyValuePair<string, object?>> parameters) = BuildQuery(request);

            await using PooledClickHouseConnection lease = await _connectionFactory.AcquireAsync(cancellationToken);
            await using ClickHouseCommand command = lease.Connection.CreateCommand();
            command.CommandText = sqlCommandText;

            foreach (KeyValuePair<string, object?> parameter in parameters)
            {
                command.Parameters.Add(new ClickHouseParameter { ParameterName = parameter.Key, Value = parameter.Value });
            }

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            DateTime firstStart = RetentionRequestFactory.Start(request.From, request.Period);
            SortedDictionary<DateTime, long[]> cohorts = [];

            while (await reader.ReadAsync(cancellationToken))
            {
                DateTime cohort = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
                int offset = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                long actors = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                int index = RetentionRequestFactory.Count(firstStart, cohort, request.Period) - 1;
                int length = request.PeriodCount - index;

                if (index < 0 || length <= 0 || offset < 0 || offset >= length)
                {
                    continue;
                }

                if (!cohorts.TryGetValue(cohort, out long[]? returning))
                {
                    returning = new long[length];
                    cohorts[cohort] = returning;
                }

                returning[offset] = actors;
            }

            List<RetentionCohort> result = new(cohorts.Count);

            foreach (KeyValuePair<DateTime, long[]> entry in cohorts)
            {
                long size = entry.Value[0];
                double[] rates = new double[entry.Value.Length];

                for (int i = 0; i < rates.Length; i++)
                {
                    rates[i] = size > 0 ? (double)entry.Value[i] / size : 0;
                }

                result.Add(new RetentionCohort { Cohort = entry.Key, Size = size, Returning = entry.Value, Rates = rates });
            }

            activity?.SetTag("nealytics.records_returned", result.Count);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return new RetentionResponse
            {
                Period = periodWire,
                By = by,
                From = request.From,
                To = request.To,
                Cohorts = result,
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
}

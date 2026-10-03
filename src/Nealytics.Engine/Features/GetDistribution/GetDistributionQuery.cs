namespace Nealytics.Engine.Features.GetDistribution;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class GetDistributionQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly ILogger<GetDistributionQuery> _logger;

    public GetDistributionQuery(ClickHouseConnectionFactory connectionFactory, ILogger<GetDistributionQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    [LoggerMessage(EventId = 6301, Level = LogLevel.Information,
        Message = "Executing distribution of {Subject} for Project: {ProjectId} / Tenant: {TenantId}.")]
    private static partial void LogQueryStarted(ILogger logger, string subject, string projectId, string tenantId);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in DistributionRequest request)
    {
        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(request.Scope, 24);

        for (int i = 0; i < request.Edges.Count; i++)
        {
            parameters.Add(new KeyValuePair<string, object?>(EdgeParameter(i), request.Edges[i]));
        }

        StringBuilder sql = new(768);
        sql.Append("SELECT count() AS n, min(v), max(v), avg(v), ");
        sql.Append(request.Approximate ? "quantilesTDigest(" : "quantilesExact(");

        for (int i = 0; i < request.Quantiles.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            sql.Append(request.Quantiles[i].ToString("R", CultureInfo.InvariantCulture));
        }

        sql.Append(")(v) AS q");

        for (int i = 0; i <= request.Edges.Count; i++)
        {
            if (request.Edges.Count == 0)
            {
                break;
            }

            sql.Append(", countIf(");

            if (i > 0)
            {
                sql.Append("v >= {").Append(EdgeParameter(i - 1)).Append(":Float64}");
            }

            if (i < request.Edges.Count)
            {
                if (i > 0)
                {
                    sql.Append(" AND ");
                }

                sql.Append("v < {").Append(EdgeParameter(i)).Append(":Float64}");
            }

            sql.Append(") AS b").Append(i);
        }

        sql.Append(" FROM (");
        AppendValues(sql, request);
        sql.Append(')');

        return (sql.ToString(), parameters);
    }

    private static void AppendValues(StringBuilder sql, in DistributionRequest request)
    {
        switch (request.Subject)
        {
            case DistributionSubject.Measure:
                sql.Append("SELECT toFloat64(").Append(request.MeasureColumn).Append(") AS v FROM ")
                    .Append(RollupRegistry.Database).Append('.').Append(RollupRegistry.SourceTable);
                ScopeClause.AppendRaw(sql, request.Scope);
                sql.Append(" AND ").Append(request.MeasureColumn).Append(" IS NOT NULL");
                return;
            case DistributionSubject.SessionDuration:
                sql.Append("SELECT toFloat64(dateDiff('millisecond', min(timestamp), max(timestamp))) AS v FROM ")
                    .Append(RollupRegistry.Database).Append('.').Append(RollupRegistry.SourceTable);
                ScopeClause.AppendRaw(sql, request.Scope);
                sql.Append(" GROUP BY session_id");
                return;
            case DistributionSubject.SessionEvents:
                sql.Append("SELECT toFloat64(count()) AS v FROM ")
                    .Append(RollupRegistry.Database).Append('.').Append(RollupRegistry.SourceTable);
                ScopeClause.AppendRaw(sql, request.Scope);
                sql.Append(" GROUP BY session_id");
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Subject, "Unhandled subject.");
        }
    }

    private static string EdgeParameter(int index) => string.Create(CultureInfo.InvariantCulture, $"edge{index}");

    public async Task<DistributionResponse> ExecuteAsync(DistributionRequest request, CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetDistributionQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("nealytics.project_id", request.ProjectId);
        activity?.SetTag("nealytics.tenant_id", request.TenantId);
        activity?.SetTag("nealytics.subject", request.Wire);

        LogQueryStarted(_logger, request.Wire, request.ProjectId, request.TenantId);
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

            long count = 0;
            double min = 0;
            double max = 0;
            double avg = 0;
            List<DistributionQuantile> quantiles = new(request.Quantiles.Count);
            List<DistributionBucket> buckets = new(request.Edges.Count + 1);

            if (await reader.ReadAsync(cancellationToken))
            {
                count = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                min = ReadNumber(reader, 1);
                max = ReadNumber(reader, 2);
                avg = ReadNumber(reader, 3);

                double[] values = count == 0 ? [] : ReadArray(reader, 4);

                for (int i = 0; i < request.Quantiles.Count; i++)
                {
                    quantiles.Add(new DistributionQuantile
                    {
                        Q = request.Quantiles[i],
                        Value = i < values.Length && double.IsFinite(values[i]) ? values[i] : 0,
                    });
                }

                for (int i = 0; request.Edges.Count > 0 && i <= request.Edges.Count; i++)
                {
                    buckets.Add(new DistributionBucket
                    {
                        From = i == 0 ? null : request.Edges[i - 1],
                        To = i == request.Edges.Count ? null : request.Edges[i],
                        Count = Convert.ToInt64(reader.GetValue(5 + i), CultureInfo.InvariantCulture),
                    });
                }
            }

            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);
            activity?.SetTag("nealytics.values", count);

            return new DistributionResponse
            {
                Of = request.Wire,
                Unit = request.Subject switch
                {
                    DistributionSubject.SessionDuration => "ms",
                    DistributionSubject.SessionEvents => "events",
                    _ => request.MeasureColumn ?? string.Empty,
                },
                EventType = request.EventType,
                From = request.From,
                To = request.To,
                Mode = request.Approximate ? "approx" : "exact",
                Count = count,
                Min = min,
                Max = max,
                Avg = avg,
                Quantiles = quantiles,
                Buckets = buckets,
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

    private static double ReadNumber(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        double value = Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        return double.IsFinite(value) ? value : 0;
    }

    private static double[] ReadArray(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return [];
        }

        object raw = reader.GetValue(ordinal);

        if (raw is double[] doubles)
        {
            return doubles;
        }

        if (raw is System.Collections.IEnumerable items)
        {
            List<double> values = [];

            foreach (object item in items)
            {
                values.Add(Convert.ToDouble(item, CultureInfo.InvariantCulture));
            }

            return values.ToArray();
        }

        return [];
    }
}

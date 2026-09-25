namespace Nealytics.Engine.Features.GetComparison;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class GetComparisonQuery
{
    private const string UngroupedPlanningKey = "event_type";

    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly QueryGuard _guard;
    private readonly RollupRegistry _rollups;
    private readonly ILogger<GetComparisonQuery> _logger;

    public GetComparisonQuery(
        ClickHouseConnectionFactory connectionFactory,
        QueryGuard guard,
        RollupRegistry rollups,
        ILogger<GetComparisonQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _guard = guard;
        _rollups = rollups;
        _logger = logger;
    }

    [LoggerMessage(EventId = 6401, Level = LogLevel.Information,
        Message = "Executing comparison of {Metric} for Project: {ProjectId} / Tenant: {TenantId}.")]
    private static partial void LogQueryStarted(ILogger logger, string metric, string projectId, string tenantId);

    public static PivotRollupPlan? Plan(in ComparisonRequest request, RollupRegistry rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        PivotRollupPlan? current = PivotRollupPlanner.Select(Planning(request, request.From, request.To), rollups);

        if (current is not PivotRollupPlan chosen)
        {
            return null;
        }

        PivotRollupPlan? previous = PivotRollupPlanner.Select(
            Planning(request, request.PreviousFrom, request.PreviousTo), rollups);

        return previous is PivotRollupPlan other && ReferenceEquals(other.Rollup, chosen.Rollup) ? chosen : null;
    }

    private static PivotRequest Planning(in ComparisonRequest request, DateTime from, DateTime to) => new()
    {
        ProjectId = request.ProjectId,
        TenantId = request.TenantId,
        GroupByColumn = request.GroupByColumn ?? UngroupedPlanningKey,
        Metrics = [request.Metric],
        Filters = request.Filters,
        TrafficClass = request.TrafficClass,
        From = from,
        To = to,
        Exact = request.Exact,
        Approximate = request.Approximate,
    };

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in ComparisonRequest request, PivotRollupPlan? plan)
    {
        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(request.Scope, 24);
        parameters.Add(new KeyValuePair<string, object?>("previousFrom", request.PreviousFrom));
        parameters.Add(new KeyValuePair<string, object?>("previousTo", request.PreviousTo));

        string timeColumn = plan is null ? "timestamp" : "bucket";
        string current = Window(timeColumn, "fromTimestamp", "toTimestamp");
        string previous = Window(timeColumn, "previousFrom", "previousTo");

        string aggregates = plan is PivotRollupPlan rollup
            ? PivotAggregates.Rollup(request.Metric, rollup.StateColumns[0], current) + " AS c, "
                + PivotAggregates.Rollup(request.Metric, rollup.StateColumns[0], previous) + " AS p"
            : PivotAggregates.Raw(request.Metric, current, request.Approximate, request.Exact) + " AS c, "
                + PivotAggregates.Raw(request.Metric, previous, request.Approximate, request.Exact) + " AS p";

        StringBuilder where = new(384);
        where.Append(ScopeClause.Tenant).Append(" AND (").Append(current).Append(" OR ").Append(previous).Append(')');
        ScopeClause.AppendConstraints(where, request.Scope, normalise: plan is null);

        string source = RollupRegistry.Database + "."
            + (plan is PivotRollupPlan routed ? routed.Rollup.TableName : RollupRegistry.SourceTable);

        StringBuilder sql = new(1024);

        if (request.GroupByColumn is null)
        {
            sql.Append("SELECT ").Append(aggregates).Append(" FROM ").Append(source).Append(where);
            return (sql.ToString(), parameters);
        }

        parameters.Add(new KeyValuePair<string, object?>("limit", request.Limit));

        sql.Append("WITH grouped AS (SELECT ");
        sql.Append(plan is null ? "ifNull(toString(" + request.GroupByColumn + "), '')" : request.GroupByColumn);
        sql.Append(" AS key, ").Append(aggregates).Append(" FROM ").Append(source).Append(where).Append(" GROUP BY key),");
        sql.Append(" totals AS (SELECT ").Append(aggregates).Append(" FROM ").Append(source).Append(where).Append(')');
        sql.Append(" SELECT key, c, p, (SELECT c FROM totals) AS tc, (SELECT p FROM totals) AS tp,");
        sql.Append(" (SELECT count() FROM grouped) AS group_count FROM grouped ORDER BY ");
        sql.Append(request.Order switch
        {
            ComparisonOrder.Previous => "p",
            ComparisonOrder.Change => "(c - p)",
            ComparisonOrder.Key => "key",
            _ => "c",
        });
        sql.Append(request.Descending ? " DESC" : " ASC");
        sql.Append(", key ASC LIMIT {limit:Int32}");

        return (sql.ToString(), parameters);
    }

    private static string Window(string column, string fromParameter, string toParameter) =>
        "(" + column + " >= {" + fromParameter + ":DateTime64} AND " + column + " < {" + toParameter + ":DateTime64})";

    public async Task<ComparisonResponse> ExecuteAsync(ComparisonRequest request, CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetComparisonQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("neavents.project_id", request.ProjectId);
        activity?.SetTag("neavents.tenant_id", request.TenantId);
        activity?.SetTag("nealytics.metric", request.Metric.Wire);

        LogQueryStarted(_logger, request.Metric.Wire, request.ProjectId, request.TenantId);
        long startTicks = Stopwatch.GetTimestamp();

        try
        {
            PivotRollupPlan? plan = Plan(request, _rollups);
            (string sqlCommandText, IReadOnlyList<KeyValuePair<string, object?>> parameters) = BuildQuery(request, plan);

            await using PooledClickHouseConnection lease = await _connectionFactory.AcquireAsync(cancellationToken);
            await using ClickHouseCommand command = lease.Connection.CreateCommand();
            command.CommandText = _guard.Limit(sqlCommandText);

            foreach (KeyValuePair<string, object?> parameter in parameters)
            {
                command.Parameters.Add(new ClickHouseParameter { ParameterName = parameter.Key, Value = parameter.Value });
            }

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            bool grouped = request.GroupByColumn is not null;
            List<ComparisonRow> rows = grouped ? new List<ComparisonRow>(request.Limit) : [];
            double empty = request.Metric.Kind == PivotMetricKind.Measure ? double.NaN : 0;
            ComparisonValue totals = ComparisonValue.Of(empty, empty);
            long groupCount = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                if (!grouped)
                {
                    totals = ComparisonValue.Of(ReadNumber(reader, 0), ReadNumber(reader, 1));
                    continue;
                }

                rows.Add(new ComparisonRow
                {
                    Key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    Value = ComparisonValue.Of(ReadNumber(reader, 1), ReadNumber(reader, 2)),
                });
                totals = ComparisonValue.Of(ReadNumber(reader, 3), ReadNumber(reader, 4));
                groupCount = Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
            }

            bool truncated = groupCount > rows.Count;
            activity?.SetTag("neavents.records_returned", rows.Count);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return new ComparisonResponse
            {
                Metric = request.Metric.Wire,
                Grain = request.Metric.Grain,
                EventType = request.Metric.EventType,
                GroupBy = request.GroupByColumn,
                TimeZone = request.TimeZone,
                Current = new ComparisonWindow { From = request.From, To = request.To },
                Previous = new ComparisonWindow { From = request.PreviousFrom, To = request.PreviousTo },
                Source = plan is PivotRollupPlan used ? "rollup:" + used.Rollup.Name : "raw",
                Totals = totals,
                GroupCount = grouped ? groupCount : 0,
                Truncated = truncated,
                Rows = rows,
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

    private static double ReadNumber(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? double.NaN : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}

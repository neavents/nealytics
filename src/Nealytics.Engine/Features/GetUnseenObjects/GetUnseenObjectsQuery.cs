namespace Nealytics.Engine.Features.GetUnseenObjects;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class GetUnseenObjectsQuery
{
    public const string CandidateTable = "candidate_ids";
    public const string CandidateColumn = "id";

    private const string ObjectColumn = "object_id";

    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly QueryGuard _guard;
    private readonly RollupRegistry _rollups;
    private readonly ILogger<GetUnseenObjectsQuery> _logger;

    public GetUnseenObjectsQuery(
        ClickHouseConnectionFactory connectionFactory,
        QueryGuard guard,
        RollupRegistry rollups,
        ILogger<GetUnseenObjectsQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _guard = guard;
        _rollups = rollups;
        _logger = logger;
    }

    [LoggerMessage(EventId = 6501, Level = LogLevel.Information,
        Message = "Executing unseen objects for Project: {ProjectId} / Tenant: {TenantId} over {CandidateCount} candidate(s).")]
    private static partial void LogQueryStarted(ILogger logger, string projectId, string tenantId, int candidateCount);

    public static Rollup? Plan(in UnseenObjectsRequest request, RollupRegistry rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        if (ScopeClause.HasMeasureFilter(request.Filters))
        {
            return null;
        }

        Rollup? best = null;

        foreach (Rollup candidate in rollups.Routable)
        {
            if (candidate.Grain == RollupGrain.Session
                || !RollupPlanner.IsAligned(request.From, request.To, candidate.Grain)
                || !candidate.CoversColumn(ObjectColumn)
                || !CoversEventTypes(request, candidate)
                || !FiltersCovered(request.Filters, candidate))
            {
                continue;
            }

            if (best is null || candidate.Dimensions.Count < best.Dimensions.Count)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool CoversEventTypes(in UnseenObjectsRequest request, Rollup rollup)
    {
        if (request.EventType is null && request.ImpressionEventType is null)
        {
            return rollup.CoversEventType(null);
        }

        return (request.EventType is null ? rollup.CoversEventType(null) : rollup.CoversEventType(request.EventType))
            && (request.ImpressionEventType is null || rollup.CoversEventType(request.ImpressionEventType));
    }

    private static bool FiltersCovered(IReadOnlyList<QueryFilter> filters, Rollup rollup)
    {
        foreach (QueryFilter filter in filters)
        {
            if (!rollup.CoversColumn(filter.Column))
            {
                return false;
            }
        }

        return true;
    }

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in UnseenObjectsRequest request, Rollup? plan)
    {
        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(request.Scope, 24);
        StringBuilder where = new(512);

        if (plan is null)
        {
            ScopeClause.AppendRaw(where, request.Scope);
        }
        else
        {
            ScopeClause.AppendRollup(where, request.Scope, "bucket");
        }

        string engaged = "1";
        string exposed = "0";

        if (request.EventType is not null)
        {
            parameters.Add(new KeyValuePair<string, object?>("eventType", request.EventType));
        }

        if (request.ImpressionEventType is not null)
        {
            parameters.Add(new KeyValuePair<string, object?>("impressionEventType", request.ImpressionEventType));
            exposed = "event_type = {impressionEventType:String}";
        }

        if (request.EventType is not null && request.ImpressionEventType is not null)
        {
            where.Append(" AND event_type IN ({eventType:String}, {impressionEventType:String})");
            engaged = "event_type = {eventType:String}";
        }
        else if (request.EventType is not null)
        {
            where.Append(" AND event_type = {eventType:String}");
        }
        else if (request.ImpressionEventType is not null)
        {
            engaged = "event_type != {impressionEventType:String}";
        }

        where.Append(" AND ").Append(ObjectColumn).Append(" IN (SELECT ").Append(CandidateColumn)
            .Append(" FROM ").Append(CandidateTable).Append(')');

        string source = RollupRegistry.Database + "." + (plan is null ? RollupRegistry.SourceTable : plan.TableName);

        StringBuilder sql = new(1024);
        sql.Append("SELECT ").Append(CandidateTable).Append('.').Append(CandidateColumn).Append(", seen.exposed FROM ")
            .Append(CandidateTable).Append(" LEFT ANY JOIN (SELECT assumeNotNull(").Append(ObjectColumn).Append(") AS id, max(")
            .Append(engaged).Append(") AS engaged, max(").Append(exposed).Append(") AS exposed FROM ").Append(source)
            .Append(where).Append(" GROUP BY id) AS seen ON ").Append(CandidateTable).Append('.').Append(CandidateColumn)
            .Append(" = seen.id WHERE seen.engaged = 0 ORDER BY ").Append(CandidateTable).Append('.').Append(CandidateColumn);

        return (sql.ToString(), parameters);
    }

    public static ClickHouseTableProvider Candidates(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ClickHouseTableProvider table = new(CandidateTable, ids.Count);
        table.Columns.AddColumn(CandidateColumn, ids);
        return table;
    }

    public async Task<UnseenObjectsResponse> ExecuteAsync(UnseenObjectsRequest request, CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetUnseenObjectsQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("neavents.project_id", request.ProjectId);
        activity?.SetTag("neavents.tenant_id", request.TenantId);
        activity?.SetTag("nealytics.candidates", request.Ids.Count);

        LogQueryStarted(_logger, request.ProjectId, request.TenantId, request.Ids.Count);
        long startTicks = Stopwatch.GetTimestamp();

        try
        {
            Rollup? plan = Plan(request, _rollups);
            (string sqlCommandText, IReadOnlyList<KeyValuePair<string, object?>> parameters) = BuildQuery(request, plan);

            await using PooledClickHouseConnection lease = await _connectionFactory.AcquireAsync(cancellationToken);
            await using ClickHouseCommand command = lease.Connection.CreateCommand();
            command.CommandText = _guard.Limit(sqlCommandText);
            command.TableProviders.Add(Candidates(request.Ids));

            foreach (KeyValuePair<string, object?> parameter in parameters)
            {
                command.Parameters.Add(new ClickHouseParameter { ParameterName = parameter.Key, Value = parameter.Value });
            }

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            List<string> unseen = [];
            List<string> impressionOnly = [];

            while (await reader.ReadAsync(cancellationToken))
            {
                string id = reader.GetString(0);

                if (reader.IsDBNull(1) || Convert.ToByte(reader.GetValue(1), CultureInfo.InvariantCulture) == 0)
                {
                    unseen.Add(id);
                }
                else
                {
                    impressionOnly.Add(id);
                }
            }

            activity?.SetTag("neavents.records_returned", unseen.Count + impressionOnly.Count);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return new UnseenObjectsResponse
            {
                From = request.From,
                To = request.To,
                EventType = request.EventType,
                ImpressionEventType = request.ImpressionEventType,
                Source = plan is null ? "raw" : "rollup:" + plan.Name,
                Candidates = request.Ids.Count,
                Seen = request.Ids.Count - unseen.Count - impressionOnly.Count,
                Unseen = unseen,
                ImpressionOnly = impressionOnly,
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

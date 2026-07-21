namespace Nealytics.Engine.Features.GetActiveUsers;

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

public sealed partial class GetActiveUsersQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly ILogger<GetActiveUsersQuery> _logger;

    public GetActiveUsersQuery(
        ClickHouseConnectionFactory connectionFactory,
        ILogger<GetActiveUsersQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information,
        Message = "Executing active-users query for Project: {ProjectId} / Tenant: {TenantId} by {Dimension} at {Interval} granularity.")]
    private static partial void LogQueryStarted(ILogger logger, string projectId, string tenantId, string dimension, string interval);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in ActiveUsersRequest request)
    {
        string bucketFunction = ActiveUsersIntervalParser.ToBucketFunction(request.Interval);
        string dimColumn = ActiveDimensionParser.ToColumn(request.Dimension);
        string uniqFunction = ActiveCountModeParser.ToUniqFunction(request.Mode);

        List<KeyValuePair<string, object?>> parameters = new List<KeyValuePair<string, object?>>(5)
        {
            new KeyValuePair<string, object?>("projectId", request.ProjectId),
            new KeyValuePair<string, object?>("tenantId", request.TenantId),
            new KeyValuePair<string, object?>("fromTimestamp", request.From),
            new KeyValuePair<string, object?>("toTimestamp", request.To),
            new KeyValuePair<string, object?>("limit", request.Limit)
        };

        StringBuilder sql = new StringBuilder(256);
        sql.Append("SELECT ");
        sql.Append(bucketFunction);
        sql.Append("(timestamp) AS bucket, ");
        sql.Append(uniqFunction);
        sql.Append('(');
        sql.Append(dimColumn);
        sql.Append(") AS active_count ");
        sql.Append("FROM nealytics_core.global_events ");
        sql.Append("WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String} ");
        sql.Append("AND timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64} ");
        sql.Append("GROUP BY bucket ORDER BY bucket ASC LIMIT {limit:Int32}");

        return (sql.ToString(), parameters);
    }

    public async Task<ActiveUsersResponse> ExecuteAsync(
        ActiveUsersRequest request,
        CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetActiveUsersQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("neavents.project_id", request.ProjectId);
        activity?.SetTag("neavents.tenant_id", request.TenantId);

        string intervalWire = ActiveUsersIntervalParser.ToWireFormat(request.Interval);
        string dimensionWire = ActiveDimensionParser.ToWireFormat(request.Dimension);
        string modeWire = ActiveCountModeParser.ToWireFormat(request.Mode);
        LogQueryStarted(_logger, request.ProjectId, request.TenantId, dimensionWire, intervalWire);
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

            List<ActiveUsersPoint> points = new List<ActiveUsersPoint>(request.Limit);

            while (await reader.ReadAsync(cancellationToken))
            {
                ActiveUsersPoint point = new ActiveUsersPoint
                {
                    Bucket = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                    ActiveCount = Convert.ToInt64(reader.GetValue(1))
                };
                points.Add(point);
            }

            activity?.SetTag("neavents.buckets_returned", points.Count);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return new ActiveUsersResponse
            {
                ProjectId = request.ProjectId,
                TenantId = request.TenantId,
                Interval = intervalWire,
                By = dimensionWire,
                Mode = modeWire,
                From = request.From,
                To = request.To,
                Points = points
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

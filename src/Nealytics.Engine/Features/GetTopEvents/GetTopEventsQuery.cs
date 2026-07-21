namespace Nealytics.Engine.Features.GetTopEvents;

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

public sealed partial class GetTopEventsQuery
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly ILogger<GetTopEventsQuery> _logger;

    public GetTopEventsQuery(
        ClickHouseConnectionFactory connectionFactory,
        ILogger<GetTopEventsQuery> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information,
        Message = "Executing top-N query for Project: {ProjectId} / Tenant: {TenantId} by {Dimension}.")]
    private static partial void LogQueryStarted(ILogger logger, string projectId, string tenantId, string dimension);

    internal static (string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters) BuildQuery(
        in TopEventsRequest request)
    {
        string dimColumn = TopDimensionParser.ToColumn(request.Dimension);

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
        sql.Append(dimColumn);
        sql.Append(" AS key, count() AS event_count ");
        sql.Append("FROM nealytics_core.global_events ");
        sql.Append("WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String} ");
        sql.Append("AND timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64}");

        if (TopDimensionParser.ExcludesNull(request.Dimension))
        {
            sql.Append(" AND ");
            sql.Append(dimColumn);
            sql.Append(" IS NOT NULL");
        }

        sql.Append(" GROUP BY key ORDER BY event_count DESC LIMIT {limit:Int32}");

        return (sql.ToString(), parameters);
    }

    public async Task<TopEventsResponse> ExecuteAsync(
        TopEventsRequest request,
        CancellationToken cancellationToken)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("GetTopEventsQuery.Execute");
        activity?.SetTag("db.system", "clickhouse");
        activity?.SetTag("db.operation", "select");
        activity?.SetTag("neavents.project_id", request.ProjectId);
        activity?.SetTag("neavents.tenant_id", request.TenantId);

        string dimensionWire = TopDimensionParser.ToWireFormat(request.Dimension);
        LogQueryStarted(_logger, request.ProjectId, request.TenantId, dimensionWire);
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

            List<TopEventItem> items = new List<TopEventItem>(request.Limit);

            while (await reader.ReadAsync(cancellationToken))
            {
                TopEventItem item = new TopEventItem
                {
                    Key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    Count = Convert.ToInt64(reader.GetValue(1))
                };
                items.Add(item);
            }

            activity?.SetTag("neavents.records_returned", items.Count);
            TelemetryDiagnostics.ReadQueriesExecuted.Add(1);

            return new TopEventsResponse
            {
                ProjectId = request.ProjectId,
                TenantId = request.TenantId,
                Dimension = dimensionWire,
                From = request.From,
                To = request.To,
                Items = items
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

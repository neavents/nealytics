namespace Nealytics.Engine.Features.BatchProcessor;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed class ClickHouseBatchWriter : ITelemetryBatchWriter
{
    private const string InsertColumns =
        "INSERT INTO nealytics_core.global_events (event_id, project_id, tenant_id, session_id, user_id, event_type, item_id, metadata_json, timestamp)";

    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly string _insertCommand;

    public ClickHouseBatchWriter(ClickHouseConnectionFactory connectionFactory, IOptions<TelemetryEngineOptions> options)
    {
        _connectionFactory = connectionFactory;
        _insertCommand = BuildInsertCommand(options.Value.EnableAsyncInsert);
    }

    internal static string BuildInsertCommand(bool asyncInsert)
    {
        if (asyncInsert)
        {
            return InsertColumns + " SETTINGS async_insert=1, wait_for_async_insert=1 VALUES";
        }

        return InsertColumns + " VALUES";
    }

    public async Task WriteAsync(
        IReadOnlyList<GlobalTelemetryPayload> batch, int count, CancellationToken cancellationToken)
    {
        Guid[] eventIds = ArrayPool<Guid>.Shared.Rent(count);
        string[] projectIds = ArrayPool<string>.Shared.Rent(count);
        string[] tenantIds = ArrayPool<string>.Shared.Rent(count);
        string[] sessionIds = ArrayPool<string>.Shared.Rent(count);
        string?[] userIds = ArrayPool<string?>.Shared.Rent(count);
        string[] eventTypes = ArrayPool<string>.Shared.Rent(count);
        string?[] itemIds = ArrayPool<string?>.Shared.Rent(count);
        string[] metadataJsons = ArrayPool<string>.Shared.Rent(count);
        DateTimeOffset[] timestamps = ArrayPool<DateTimeOffset>.Shared.Rent(count);

        try
        {
            TelemetryColumnMapper.Fill(
                batch, count, eventIds, projectIds, tenantIds, sessionIds,
                userIds, eventTypes, itemIds, metadataJsons, timestamps);

            Dictionary<string, object?> columns = TelemetryColumnMapper.BuildColumns(
                count, eventIds, projectIds, tenantIds, sessionIds,
                userIds, eventTypes, itemIds, metadataJsons, timestamps);

            await using PooledClickHouseConnection lease =
                await _connectionFactory.AcquireAsync(cancellationToken);

            await using ClickHouseColumnWriter writer =
                await lease.Connection.CreateColumnWriterAsync(_insertCommand, cancellationToken);

            await writer.WriteTableAsync(columns, count, cancellationToken);
            await writer.EndWriteAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<Guid>.Shared.Return(eventIds);
            ArrayPool<string>.Shared.Return(projectIds, true);
            ArrayPool<string>.Shared.Return(tenantIds, true);
            ArrayPool<string>.Shared.Return(sessionIds, true);
            ArrayPool<string?>.Shared.Return(userIds, true);
            ArrayPool<string>.Shared.Return(eventTypes, true);
            ArrayPool<string?>.Shared.Return(itemIds, true);
            ArrayPool<string>.Shared.Return(metadataJsons, true);
            ArrayPool<DateTimeOffset>.Shared.Return(timestamps);
        }
    }
}

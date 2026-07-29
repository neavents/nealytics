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
    internal const string InsertColumns =
        "INSERT INTO nealytics_core.global_events (event_id, project_id, tenant_id, session_id, "
        + "user_id, event_type, item_id, menu_id, section_id, table_id, device_class, os, browser, "
        + "country, metadata_json, timestamp)";

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
        // Buffers own their own rent/return; see TelemetryColumnBuffers for why.
        using TelemetryColumnBuffers buffers = new(count);

        buffers.Fill(batch);
        Dictionary<string, object?> columns = buffers.BuildColumns();

        await using PooledClickHouseConnection lease =
            await _connectionFactory.AcquireAsync(cancellationToken);

        try
        {
            await using ClickHouseColumnWriter writer =
                await lease.Connection.CreateColumnWriterAsync(_insertCommand, cancellationToken);

            await writer.WriteTableAsync(columns, count, cancellationToken);
            await writer.EndWriteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not put this connection back. A ClickHouse restart leaves connections reporting
            // Open while being unusable, so a pooled corpse is handed straight back to the next
            // retry — which is how a database blip that resolved itself in seconds turned into a
            // service that needed restarting. Throwing away a healthy connection costs one
            // reconnect; keeping a dead one costs every batch after it.
            lease.Discard();
            throw;
        }
    }
}

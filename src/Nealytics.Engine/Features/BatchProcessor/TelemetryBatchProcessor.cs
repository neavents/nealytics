namespace Nealytics.Engine.Features.BatchProcessor;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class TelemetryBatchProcessor : BackgroundService
{
    private readonly TelemetryChannelBroker _broker;
    private readonly WriteAheadLogger _wal;
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<TelemetryBatchProcessor> _logger;
    private readonly List<GlobalTelemetryPayload> _batchBuffer;
    private readonly TelemetryEngineOptions _options;
    private int _consecutiveFailures;

    private const int DrainTimeoutSeconds = 30;
    public TelemetryBatchProcessor(
        TelemetryChannelBroker broker,
        WriteAheadLogger wal,
        ClickHouseConnectionFactory connectionFactory,
        IHostApplicationLifetime lifetime,
        IOptions<TelemetryEngineOptions> options,
        ILogger<TelemetryBatchProcessor> logger)
    {
        _broker = broker;
        _wal = wal;
        _connectionFactory = connectionFactory;
        _lifetime = lifetime;
        _options = options.Value;
        _logger = logger;
        _batchBuffer = new List<GlobalTelemetryPayload>(_options.DatabaseBatchCommitSize);
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Warning,
        Message = "Host termination detected. Flushing in-memory pipeline.")]
    private static partial void LogEmergencyDrainActive(ILogger logger);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information,
        Message = "Flushing batch of {Count} events to ClickHouse.")]
    private static partial void LogStorageFlush(ILogger logger, int count);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Critical,
        Message = "ClickHouse batch insert failed after all retries. Events preserved in WAL.")]
    private static partial void LogBatchInsertFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9004, Level = LogLevel.Warning,
        Message = "ClickHouse insert attempt {Attempt}/{MaxRetries} failed. Retrying in {DelayMs}ms.")]
    private static partial void LogRetryAttempt(ILogger logger, int attempt, int maxRetries, int delayMs);

    [LoggerMessage(EventId = 9005, Level = LogLevel.Information,
        Message = "WAL replay recovered {Count} uncommitted events.")]
    private static partial void LogWalRecovery(ILogger logger, int count);

    [LoggerMessage(EventId = 9006, Level = LogLevel.Warning,
        Message = "WAL replay batch failed. Retrying in 10 seconds.")]
    private static partial void LogWalReplayRetry(ILogger logger);

    [LoggerMessage(EventId = 9007, Level = LogLevel.Warning,
        Message = "Batch insert failed. Backing off for {DelayMs}ms before next batch (consecutive failures: {Failures}).")]
    private static partial void LogCrossBatchBackoff(ILogger logger, int delayMs, int failures);

    [LoggerMessage(EventId = 9008, Level = LogLevel.Critical,
        Message = "Shutdown drain timed out after {Seconds}s. {RemainingCount} events may be lost.")]
    private static partial void LogDrainTimeout(ILogger logger, int seconds, int remainingCount);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lifetime.ApplicationStopping.Register(() => LogEmergencyDrainActive(_logger));

        await RecoverWalEntriesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using CancellationTokenSource timeoutSource =
                    new CancellationTokenSource(TimeSpan.FromSeconds(_options.ForceFlushIntervalSeconds));
                using CancellationTokenSource linkedSource =
                    CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutSource.Token);

                while (_batchBuffer.Count < _options.DatabaseBatchCommitSize)
                {
                    GlobalTelemetryPayload item = await _broker.Reader.ReadAsync(linkedSource.Token);
                    _batchBuffer.Add(item);
                }

                bool committed = await PushBatchToClickHouseAsync(stoppingToken);

                if (committed)
                {
                    _consecutiveFailures = 0;
                }
                else
                {
                    int delayMs = Math.Min(1000 * (1 << _consecutiveFailures), 30_000);
                    LogCrossBatchBackoff(_logger, delayMs, _consecutiveFailures + 1);
                    await Task.Delay(delayMs, stoppingToken);
                    _consecutiveFailures++;
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                if (_batchBuffer.Count > 0)
                {
                    bool committed = await PushBatchToClickHouseAsync(stoppingToken);
                    if (committed)
                        _consecutiveFailures = 0;
                }
            }
        }

        await ExecuteCriticalDrainLoopAsync();
    }

    private async Task RecoverWalEntriesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GlobalTelemetryPayload> recovered = await _wal.ReplayUncommittedAsync();
        if (recovered.Count == 0)
        {
            return;
        }

        LogWalRecovery(_logger, recovered.Count);

        int offset = 0;

        while (offset < recovered.Count && !cancellationToken.IsCancellationRequested)
        {
            int remaining = recovered.Count - offset;
            int batchSize = Math.Min(remaining, _options.DatabaseBatchCommitSize);

            for (int i = 0; i < batchSize; i++)
            {
                _batchBuffer.Add(recovered[offset + i]);
            }

            bool success = await PushBatchToClickHouseAsync(cancellationToken, truncateWalOnSuccess: false);

            if (success)
            {
                offset += batchSize;
            }
            else
            {
                LogWalReplayRetry(_logger);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }

        if (offset == recovered.Count)
        {
            await _wal.TruncateAsync();
        }
    }

    private async Task ExecuteCriticalDrainLoopAsync()
    {
        using CancellationTokenSource drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(DrainTimeoutSeconds));

        while (_broker.Reader.TryRead(out GlobalTelemetryPayload? remainingItem))
        {
            _batchBuffer.Add(remainingItem);
            if (_batchBuffer.Count >= _options.DatabaseBatchCommitSize)
            {
                await PushBatchToClickHouseAsync(drainTimeout.Token);
            }
        }

        if (_batchBuffer.Count > 0)
        {
            try
            {
                await PushBatchToClickHouseAsync(drainTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                LogDrainTimeout(_logger, DrainTimeoutSeconds, _batchBuffer.Count);
            }
        }
    }

    private async Task<bool> PushBatchToClickHouseAsync(
        CancellationToken cancellationToken, bool truncateWalOnSuccess = true)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("BatchProcessor.Flush");
        long startTicks = Stopwatch.GetTimestamp();
        int batchCount = _batchBuffer.Count;
        LogStorageFlush(_logger, batchCount);

        Guid[] eventIds = ArrayPool<Guid>.Shared.Rent(batchCount);
        string[] projectIds = ArrayPool<string>.Shared.Rent(batchCount);
        string[] tenantIds = ArrayPool<string>.Shared.Rent(batchCount);
        string[] sessionIds = ArrayPool<string>.Shared.Rent(batchCount);
        string[] eventTypes = ArrayPool<string>.Shared.Rent(batchCount);
        string?[] itemIds = ArrayPool<string?>.Shared.Rent(batchCount);
        string[] metadataJsons = ArrayPool<string>.Shared.Rent(batchCount);
        DateTimeOffset[] timestamps = ArrayPool<DateTimeOffset>.Shared.Rent(batchCount);

        bool committed = false;

        try
        {
            for (int i = 0; i < batchCount; i++)
            {
                GlobalTelemetryPayload payload = _batchBuffer[i];
                eventIds[i] = payload.EventId;
                projectIds[i] = payload.ProjectId;
                tenantIds[i] = payload.TenantId;
                sessionIds[i] = payload.SessionId;
                eventTypes[i] = payload.EventType;
                itemIds[i] = payload.ItemId;
                metadataJsons[i] = payload.MetadataJson;
                timestamps[i] = new DateTimeOffset(DateTime.SpecifyKind(payload.Timestamp, DateTimeKind.Utc), TimeSpan.Zero);
            }

            Dictionary<string, object?> columns = new Dictionary<string, object?>(8)
            {
                ["event_id"] = new ArraySegment<Guid>(eventIds, 0, batchCount),
                ["project_id"] = new ArraySegment<string>(projectIds, 0, batchCount),
                ["tenant_id"] = new ArraySegment<string>(tenantIds, 0, batchCount),
                ["session_id"] = new ArraySegment<string>(sessionIds, 0, batchCount),
                ["event_type"] = new ArraySegment<string>(eventTypes, 0, batchCount),
                ["item_id"] = new ArraySegment<string?>(itemIds, 0, batchCount),
                ["metadata_json"] = new ArraySegment<string>(metadataJsons, 0, batchCount),
                ["timestamp"] = new ArraySegment<DateTimeOffset>(timestamps, 0, batchCount)
            };

            Exception? lastException = null;

            for (int attempt = 1; attempt <= _options.MaxInsertRetries; attempt++)
            {
                try
                {
                    await using PooledClickHouseConnection lease =
                        await _connectionFactory.AcquireAsync(cancellationToken);

                    await using ClickHouseColumnWriter writer = await lease.Connection.CreateColumnWriterAsync(
                        "INSERT INTO nealytics_core.global_events (event_id, project_id, tenant_id, session_id, event_type, item_id, metadata_json, timestamp) VALUES",
                        cancellationToken);

                    await writer.WriteTableAsync(columns, batchCount, cancellationToken);
                    await writer.EndWriteAsync(cancellationToken);
                    committed = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (attempt < _options.MaxInsertRetries)
                    {
                        int delayMs = Math.Min(1000 * (1 << (attempt - 1)), _options.RetryBackoffCeilingMs);
                        LogRetryAttempt(_logger, attempt, _options.MaxInsertRetries, delayMs);
                        await Task.Delay(delayMs, cancellationToken);
                    }
                }
            }

            if (committed)
            {
                if (truncateWalOnSuccess)
                {
                    await _wal.TruncateIfAllCommittedAsync(() => _broker.Reader.Count == 0);
                }

                TelemetryDiagnostics.StorageBatchesCommitted.Add(1);
            }
            else if (lastException is not null)
            {
                LogBatchInsertFailed(_logger, lastException);
            }
        }
        finally
        {
            ArrayPool<Guid>.Shared.Return(eventIds);
            ArrayPool<string>.Shared.Return(projectIds, true);
            ArrayPool<string>.Shared.Return(tenantIds, true);
            ArrayPool<string>.Shared.Return(sessionIds, true);
            ArrayPool<string>.Shared.Return(eventTypes, true);
            ArrayPool<string?>.Shared.Return(itemIds, true);
            ArrayPool<string>.Shared.Return(metadataJsons, true);
            ArrayPool<DateTimeOffset>.Shared.Return(timestamps);

            long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
            double executionSeconds = (double)elapsedTicks / Stopwatch.Frequency;
            TelemetryDiagnostics.StorageWriteDuration.Record(executionSeconds);

            TelemetryDiagnostics.DecrementQueueCounter(batchCount);
            _batchBuffer.Clear();
        }

        return committed;
    }
}

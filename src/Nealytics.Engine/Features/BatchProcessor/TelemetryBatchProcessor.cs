namespace Nealytics.Engine.Features.BatchProcessor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

public sealed partial class TelemetryBatchProcessor : BackgroundService
{
    private readonly TelemetryChannelBroker _broker;
    private readonly WriteAheadLogger _wal;
    private readonly ITelemetryBatchWriter _writer;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<TelemetryBatchProcessor> _logger;
    private readonly List<GlobalTelemetryPayload> _bufferA;
    private readonly List<GlobalTelemetryPayload> _bufferB;
    private readonly TelemetryEngineOptions _options;
    private int _consecutiveFailures;
    private GlobalTelemetryPayload? _deferredPayload;

    private const int DrainTimeoutSeconds = 30;

    public TelemetryBatchProcessor(
        TelemetryChannelBroker broker,
        WriteAheadLogger wal,
        ITelemetryBatchWriter writer,
        IHostApplicationLifetime lifetime,
        IOptions<TelemetryEngineOptions> options,
        ILogger<TelemetryBatchProcessor> logger)
    {
        _broker = broker;
        _wal = wal;
        _writer = writer;
        _lifetime = lifetime;
        _options = options.Value;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.DatabaseBatchCommitSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.DatabaseBatchByteCapacity, 1);
        _logger = logger;
        _bufferA = new List<GlobalTelemetryPayload>(_options.DatabaseBatchCommitSize);
        _bufferB = new List<GlobalTelemetryPayload>(_options.DatabaseBatchCommitSize);
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
    private static partial void LogWalRecovery(ILogger logger, long count);

    [LoggerMessage(EventId = 9006, Level = LogLevel.Warning,
        Message = "WAL replay batch failed. Retrying in {DelayMs}ms.")]
    private static partial void LogWalReplayRetry(ILogger logger, int delayMs);

    [LoggerMessage(EventId = 9007, Level = LogLevel.Warning,
        Message = "Batch insert failed. Backing off for {DelayMs}ms before next batch (consecutive failures: {Failures}).")]
    private static partial void LogCrossBatchBackoff(ILogger logger, int delayMs, int failures);

    [LoggerMessage(EventId = 9008, Level = LogLevel.Critical,
        Message = "Shutdown drain stopped with {RemainingCount} events preserved in WAL; time budget {Seconds}s.")]
    private static partial void LogDrainTimeout(ILogger logger, int seconds, long remainingCount);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lifetime.ApplicationStopping.Register(() => LogEmergencyDrainActive(_logger));

        await RecoverWalEntriesAsync(stoppingToken);

        List<GlobalTelemetryPayload> fillBuffer = _bufferA;
        Task<bool>? pendingInsert = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using CancellationTokenSource timeoutSource =
                    new CancellationTokenSource(TimeSpan.FromSeconds(_options.ForceFlushIntervalSeconds));
                using CancellationTokenSource linkedSource =
                    CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutSource.Token);

                try
                {
                    int bytes = BufferBytes(fillBuffer);
                    while (fillBuffer.Count < _options.DatabaseBatchCommitSize)
                    {
                        GlobalTelemetryPayload item = _deferredPayload
                            ?? await _broker.Reader.ReadAsync(linkedSource.Token);
                        _deferredPayload = null;
                        if (!FitsBatch(item, ref bytes))
                        {
                            _deferredPayload = item;
                            break;
                        }
                        fillBuffer.Add(item);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                }

                if (fillBuffer.Count == 0)
                {
                    continue;
                }

                if (pendingInsert is not null)
                {
                    await AwaitInsertAndBackoffAsync(pendingInsert, stoppingToken);
                }

                pendingInsert = PushBatchToClickHouseAsync(fillBuffer, stoppingToken);
                fillBuffer = ReferenceEquals(fillBuffer, _bufferA) ? _bufferB : _bufferA;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (pendingInsert is not null)
        {
            try
            {
                await AwaitInsertAndBackoffAsync(pendingInsert, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await ExecuteCriticalDrainLoopAsync(fillBuffer);
    }

    private async Task AwaitInsertAndBackoffAsync(Task<bool> insert, CancellationToken stoppingToken)
    {
        bool committed = await insert;

        if (committed)
        {
            _consecutiveFailures = 0;
            return;
        }

        int delayMs = TelemetryInsertMath.ComputeCrossBatchBackoffMs(_consecutiveFailures);
        LogCrossBatchBackoff(_logger, delayMs, _consecutiveFailures + 1);
        await Task.Delay(delayMs, stoppingToken);
        _consecutiveFailures++;
    }

    private async Task ExecuteCriticalDrainLoopAsync(List<GlobalTelemetryPayload> buffer)
    {
        using CancellationTokenSource drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(DrainTimeoutSeconds));

        try
        {
            if (buffer.Count >= _options.DatabaseBatchCommitSize)
            {
                await PushBatchToClickHouseAsync(buffer, drainTimeout.Token);
            }
            int bytes = BufferBytes(buffer);
            while (_deferredPayload is not null || _broker.Reader.TryPeek(out _))
            {
                GlobalTelemetryPayload remainingItem = _deferredPayload ?? await _broker.Reader.ReadAsync(drainTimeout.Token);
                _deferredPayload = null;
                if (!FitsBatch(remainingItem, ref bytes))
                {
                    await PushBatchToClickHouseAsync(buffer, drainTimeout.Token);
                    bytes = 0;
                    FitsBatch(remainingItem, ref bytes);
                }
                buffer.Add(remainingItem);
                if (buffer.Count >= _options.DatabaseBatchCommitSize)
                {
                    await PushBatchToClickHouseAsync(buffer, drainTimeout.Token);
                    bytes = 0;
                }
            }
            if (buffer.Count > 0)
            {
                await PushBatchToClickHouseAsync(buffer, drainTimeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            LogDrainTimeout(_logger, DrainTimeoutSeconds, _wal.UncommittedRecordCount);
        }
    }

    private static int BufferBytes(List<GlobalTelemetryPayload> buffer)
    {
        int bytes = 0;
        foreach (GlobalTelemetryPayload payload in buffer)
        {
            bytes = checked(bytes + TelemetryPayloadMemory.Estimate(payload));
        }
        return bytes;
    }

    private bool FitsBatch(GlobalTelemetryPayload payload, ref int bytes)
    {
        int required = TelemetryPayloadMemory.Estimate(payload);
        if (required > _options.DatabaseBatchByteCapacity)
        {
            throw new IOException("A retained WAL event exceeds DatabaseBatchByteCapacity; raise the capacity to recover it.");
        }
        if (required > _options.DatabaseBatchByteCapacity - bytes)
        {
            return false;
        }
        bytes += required;
        return true;
    }

    private async Task<bool> PushBatchToClickHouseAsync(
        List<GlobalTelemetryPayload> buffer, CancellationToken cancellationToken, bool replay = false)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("BatchProcessor.Flush");
        long startTicks = Stopwatch.GetTimestamp();
        int batchCount = buffer.Count;
        LogStorageFlush(_logger, batchCount);

        bool committed = false;

        try
        {
            Exception? lastException = null;

            for (int attempt = 1; attempt <= _options.MaxInsertRetries; attempt++)
            {
                try
                {
                    await _writer.WriteAsync(buffer, batchCount, cancellationToken);
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
                        int delayMs = TelemetryInsertMath.ComputeRetryBackoffMs(attempt, _options.RetryBackoffCeilingMs);
                        LogRetryAttempt(_logger, attempt, _options.MaxInsertRetries, delayMs);
                        await Task.Delay(delayMs, cancellationToken);
                    }
                }
            }

            if (committed)
            {
                if (!replay)
                {
                    await _wal.AcknowledgeCommitAsync(batchCount);
                    if (_writer is IDeliveryCheckpoint journaled)
                    {
                        await journaled.CheckpointAsync();
                    }
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
            long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
            double executionSeconds = (double)elapsedTicks / Stopwatch.Frequency;
            TelemetryDiagnostics.StorageWriteDuration.Record(executionSeconds);

            if (!replay)
            {
                TelemetryDiagnostics.DecrementQueueCounter(batchCount);
            }
            if (!replay || committed)
            {
                buffer.Clear();
            }
        }

        return committed;
    }
}

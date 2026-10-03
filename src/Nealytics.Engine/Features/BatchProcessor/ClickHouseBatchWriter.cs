namespace Nealytics.Engine.Features.BatchProcessor;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

public sealed partial class ClickHouseBatchWriter : ITelemetryBatchWriter
{
    private readonly ClickHouseConnectionFactory _connectionFactory;
    private readonly TelemetryColumnLayout _layout;
    private readonly ILogger<ClickHouseBatchWriter> _logger;
    private readonly string _insertCommand;

    public ClickHouseBatchWriter(
        ClickHouseConnectionFactory connectionFactory,
        DimensionRegistry registry,
        MeasureRegistry measures,
        IOptions<TelemetryEngineOptions> options,
        ILogger<ClickHouseBatchWriter> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _layout = new TelemetryColumnLayout(registry, measures);
        _insertCommand = BuildInsertCommand(_layout, options.Value.EnableAsyncInsert);
    }

    [LoggerMessage(EventId = 9101, Level = LogLevel.Warning,
        Message = "Dimension '{Dimension}' rejected {RejectedCount} value(s) in a batch of {BatchCount}: "
            + "they did not parse as the declared type {DeclaredType}. Those cells were written NULL.")]
    private static partial void LogDimensionValuesRejected(
        ILogger logger, string dimension, int rejectedCount, int batchCount, string declaredType);

    [LoggerMessage(EventId = 9102, Level = LogLevel.Warning,
        Message = "Measure '{Measure}' rejected {RejectedCount} value(s) in a batch of {BatchCount}: "
            + "they did not parse as the declared type {DeclaredType}, or fell outside its declared "
            + "bounds. Those cells were written NULL.")]
    private static partial void LogMeasureValuesRejected(
        ILogger logger, string measure, int rejectedCount, int batchCount, string declaredType);

    internal static string BuildInsertColumns(TelemetryColumnLayout layout)
    {
        StringBuilder builder = new(256);
        builder.Append("INSERT INTO nealytics_core.global_events (");

        for (int i = 0; i < layout.ColumnNames.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(layout.ColumnNames[i]);
        }

        builder.Append(')');
        return builder.ToString();
    }

    internal static string BuildInsertCommand(TelemetryColumnLayout layout, bool asyncInsert)
    {
        string columns = BuildInsertColumns(layout);

        if (asyncInsert)
        {
            return columns + " SETTINGS async_insert=1, wait_for_async_insert=1 VALUES";
        }

        return columns + " VALUES";
    }

    public async Task WriteAsync(
        IReadOnlyList<GlobalTelemetryPayload> batch, int count, CancellationToken cancellationToken)
    {
        using TelemetryColumnBuffers buffers = new(count, _layout);

        buffers.Fill(batch);
        ReportRejectedDimensionValues(buffers, count);
        ReportRejectedMeasureValues(buffers, count);
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
            lease.Discard();
            throw;
        }
    }

    private void ReportRejectedDimensionValues(TelemetryColumnBuffers buffers, int count)
    {
        IReadOnlyList<DimensionColumnBuffer> dimensionBuffers = buffers.DimensionBuffers;

        for (int i = 0; i < dimensionBuffers.Count; i++)
        {
            DimensionColumnBuffer buffer = dimensionBuffers[i];
            if (buffer.RejectedValueCount == 0)
            {
                continue;
            }

            LogDimensionValuesRejected(
                _logger,
                buffer.Dimension.Name,
                buffer.RejectedValueCount,
                count,
                buffer.Dimension.ConfiguredType);

            TelemetryDiagnostics.DimensionValuesRejected.Add(
                buffer.RejectedValueCount,
                new KeyValuePair<string, object?>("dimension", buffer.Dimension.Name));
        }
    }

    private void ReportRejectedMeasureValues(TelemetryColumnBuffers buffers, int count)
    {
        IReadOnlyList<MeasureColumnBuffer> measureBuffers = buffers.MeasureBuffers;

        for (int i = 0; i < measureBuffers.Count; i++)
        {
            MeasureColumnBuffer buffer = measureBuffers[i];
            if (buffer.RejectedValueCount == 0)
            {
                continue;
            }

            LogMeasureValuesRejected(
                _logger,
                buffer.Measure.Name,
                buffer.RejectedValueCount,
                count,
                buffer.Measure.ConfiguredType);

            TelemetryDiagnostics.MeasureValuesRejected.Add(
                buffer.RejectedValueCount,
                new KeyValuePair<string, object?>("measure", buffer.Measure.Name));
        }
    }
}

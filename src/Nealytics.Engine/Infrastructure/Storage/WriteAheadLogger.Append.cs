namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Serialization;

public sealed partial class WriteAheadLogger
{
    public async Task AppendAsync(GlobalTelemetryPayload payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _operations.Enter();
        try
        {
            PendingAppend pending;
            await _serializationLock.WaitAsync(cancellationToken);
            try
            {
                _operations.ThrowIfFailed();
                pending = await EnqueueSerializedAsync(payload, cancellationToken);
            }
            finally
            {
                _serializationLock.Release();
            }

            await pending.Completion.Task;
        }
        finally
        {
            _operations.Exit();
        }
    }

    private async Task<PendingAppend> EnqueueSerializedAsync(
        GlobalTelemetryPayload payload, CancellationToken cancellationToken)
    {
        _recordBuffer.Reset();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(_recordBuffer, new JsonWriterOptions { SkipValidation = true }))
        {
            JsonSerializer.Serialize(writer, payload, TelemetryAotContext.Default.GlobalTelemetryPayload);
        }
        _recordBuffer.GetSpan(1)[0] = (byte)'\n';
        _recordBuffer.Advance(1);

        int length = _recordBuffer.WrittenMemory.Length;
        await _admission.ReserveAsync(length, cancellationToken);
        byte[]? rented = null;
        bool queued = false;
        try
        {
            rented = _recordPool.Rent(length);
            _recordBuffer.WrittenMemory.CopyTo(rented);
            PendingAppend pending = new PendingAppend(rented, length);
            if (!_pending.Writer.TryWrite(pending))
            {
                _operations.ThrowIfFailed();
                throw new ObjectDisposedException(nameof(WriteAheadLogger));
            }

            queued = true;
            return pending;
        }
        finally
        {
            if (!queued)
            {
                if (rented is not null)
                {
                    _recordPool.Return(rented, clearArray: true);
                }
                _admission.Release(length);
            }
        }
    }

    private async Task RunFlushLoopAsync()
    {
        ChannelReader<PendingAppend> reader = _pending.Reader;
        List<PendingAppend> group = new List<PendingAppend>(Math.Min(_limits.FlushRecords, 256));
        try
        {
            while (await reader.WaitToReadAsync())
            {
                int bytes = 0;
                while (group.Count < _limits.FlushRecords
                    && reader.TryPeek(out PendingAppend? next)
                    && next.Length <= _limits.FlushBytes - bytes)
                {
                    PendingAppend entry = await reader.ReadAsync();
                    group.Add(entry);
                    bytes += entry.Length;
                }

                await FlushGroupAsync(group);
                CompleteGroup(group, null);
                group.Clear();
            }
        }
        catch (Exception error)
        {
            Exception failure = SignalFailure(error);
            CompleteGroup(group, failure);
            while (reader.TryRead(out PendingAppend? entry))
            {
                CompleteAppend(entry, failure);
            }
        }
    }

    private async Task FlushGroupAsync(List<PendingAppend> group)
    {
        long flushStartTicks = Stopwatch.GetTimestamp();
        await _fileLock.WaitAsync();
        try
        {
            _operations.ThrowIfFailed();
            foreach (PendingAppend entry in group)
            {
                await _file.AppendAsync(entry.Buffer.AsMemory(0, entry.Length));
            }
            await _file.FlushAsync();

            Interlocked.Add(ref _uncommittedRecords, group.Count);
            Interlocked.Increment(ref _flushGroupCount);
            Interlocked.Add(ref _flushedRecordCount, group.Count);
            Interlocked.Add(ref _flushDurationTicks, Stopwatch.GetTimestamp() - flushStartTicks);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private void CompleteGroup(List<PendingAppend> group, Exception? error)
    {
        foreach (PendingAppend entry in group)
        {
            CompleteAppend(entry, error);
        }
    }

    private void CompleteAppend(PendingAppend entry, Exception? error)
    {
        _recordPool.Return(entry.Buffer, clearArray: true);
        _admission.Release(entry.Length);
        if (error is null)
        {
            entry.Completion.TrySetResult();
        }
        else
        {
            entry.Completion.TrySetException(error);
        }
    }

    private sealed class PendingAppend
    {
        public PendingAppend(byte[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }

        public byte[] Buffer { get; }
        public int Length { get; }
        public TaskCompletionSource Completion { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

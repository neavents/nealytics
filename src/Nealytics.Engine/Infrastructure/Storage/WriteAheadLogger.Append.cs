namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage.Wal;

public sealed partial class WriteAheadLogger
{
    public async Task AppendAsync(GlobalTelemetryPayload payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _operations.Enter();
        try
        {
            PendingAppend pending;
            await _synchronization.Serialization.WaitAsync(cancellationToken);
            try
            {
                _operations.ThrowIfFailed();
                pending = await EnqueueSerializedAsync(payload, cancellationToken);
            }
            finally
            {
                _synchronization.Serialization.Release();
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
        payload.WalRecordId = Guid.NewGuid();
        _recordBuffer.Reset();
        WalRecordEncoding.Serialize(payload, _recordBuffer);
        return await EnqueueEncodedAsync(_recordBuffer.WrittenMemory, 1, cancellationToken);
    }

    private async Task<PendingAppend> EnqueueEncodedAsync(
        ReadOnlyMemory<byte> serialized, int records, CancellationToken cancellationToken)
    {
        int length = serialized.Length;
        await _admission.ReserveAsync(records, length, cancellationToken);
        byte[]? rented = null;
        bool queued = false;
        bool reserved = false;
        try
        {
            _backlog.Reserve(length);
            reserved = true;
            rented = _recordPool.Rent(length);
            serialized.CopyTo(rented);
            PendingAppend pending = new PendingAppend(rented, length, records);
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
                _admission.Release(records, length);
                if (reserved)
                {
                    _backlog.Release(length);
                }
            }
        }
    }

    private async Task RunFlushLoopAsync()
    {
        ChannelReader<PendingAppend> reader = _pending.Reader;
        List<PendingAppend> group = new List<PendingAppend>(_limits.InitialGroupCapacity);
        try
        {
            while (await reader.WaitToReadAsync())
            {
                int bytes = 0;
                int records = 0;
                while (reader.TryPeek(out PendingAppend? next)
                    && next.Records <= _limits.FlushRecords - records
                    && next.Length <= _limits.FlushBytes - bytes)
                {
                    PendingAppend entry = await reader.ReadAsync();
                    group.Add(entry);
                    bytes += entry.Length;
                    records += entry.Records;
                }

                await FlushGroupAsync(group, records);
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

    private async Task FlushGroupAsync(List<PendingAppend> group, int records)
    {
        long flushStartTicks = Stopwatch.GetTimestamp();
        await _synchronization.File.WaitAsync();
        try
        {
            _operations.ThrowIfFailed();
            foreach (PendingAppend entry in group)
            {
                await _file.AppendAsync(entry.Buffer.AsMemory(0, entry.Length));
                _persistedBytes += entry.Length;
            }
            await _file.FlushAsync();

            Interlocked.Add(ref _uncommittedRecords, records);
            _statistics.Record(records, Stopwatch.GetElapsedTime(flushStartTicks));
        }
        catch (Exception error)
        {
            throw SignalFailure(error);
        }
        finally
        {
            _synchronization.File.Release();
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
        _admission.Release(entry.Records, entry.Length);
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
        public PendingAppend(byte[] buffer, int length, int records)
        {
            Buffer = buffer;
            Length = length;
            Records = records;
        }

        public byte[] Buffer { get; }
        public int Length { get; }
        public int Records { get; }
        public TaskCompletionSource Completion { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage.Wal;

public sealed partial class WriteAheadLogger
{
    internal async Task<WalBatchAppendResult> AppendBatchAsync(
        IReadOnlyList<GlobalTelemetryPayload> payloads, CancellationToken cancellationToken, int startIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startIndex, payloads.Count);
        cancellationToken.ThrowIfCancellationRequested();
        _operations.Enter();
        try
        {
            if (startIndex == payloads.Count)
            {
                return new WalBatchAppendResult(0, null);
            }
            PendingAppend? pending = null;
            WalBatchAppendResult result;
            await _synchronization.Serialization.WaitAsync(cancellationToken);
            try
            {
                _operations.ThrowIfFailed();
                result = EncodeBatch(payloads, startIndex);
                if (result.Count > 0)
                {
                    pending = await EnqueueEncodedAsync(_batchBuffer.WrittenMemory, result.Count, cancellationToken);
                }
            }
            finally
            {
                _synchronization.Serialization.Release();
            }
            if (pending is not null)
            {
                await pending.Completion.Task;
            }
            return result;
        }
        finally
        {
            _operations.Exit();
        }
    }

    private WalBatchAppendResult EncodeBatch(IReadOnlyList<GlobalTelemetryPayload> payloads, int startIndex)
    {
        _batchBuffer.Reset();
        int count = 0;
        ExceptionDispatchInfo? failure = null;
        try
        {
            while (count < payloads.Count - startIndex && count < _limits.FlushRecords)
            {
                GlobalTelemetryPayload payload = payloads[startIndex + count];
                ArgumentNullException.ThrowIfNull(payload);
                _recordBuffer.Reset();
                WalRecordEncoding.Serialize(payload, _recordBuffer);
                ReadOnlyMemory<byte> record = _recordBuffer.WrittenMemory;
                if (record.Length > _limits.FlushBytes - _batchBuffer.WrittenMemory.Length)
                {
                    break;
                }
                record.CopyTo(_batchBuffer.GetMemory(record.Length));
                _batchBuffer.Advance(record.Length);
                count++;
            }
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
        }
        return new WalBatchAppendResult(count, failure);
    }
}

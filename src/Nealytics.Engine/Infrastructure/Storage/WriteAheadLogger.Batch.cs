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
        IReadOnlyList<GlobalTelemetryPayload> payloads, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        cancellationToken.ThrowIfCancellationRequested();
        _operations.Enter();
        try
        {
            if (payloads.Count == 0)
            {
                return new WalBatchAppendResult(0, null);
            }
            PendingAppend? pending = null;
            WalBatchAppendResult result;
            await _synchronization.Serialization.WaitAsync(cancellationToken);
            try
            {
                _operations.ThrowIfFailed();
                result = EncodeBatch(payloads);
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

    private WalBatchAppendResult EncodeBatch(IReadOnlyList<GlobalTelemetryPayload> payloads)
    {
        _batchBuffer.Reset();
        int count = 0;
        ExceptionDispatchInfo? failure = null;
        try
        {
            while (count < payloads.Count && count < _limits.FlushRecords)
            {
                ArgumentNullException.ThrowIfNull(payloads[count]);
                _recordBuffer.Reset();
                WalRecordEncoding.Serialize(payloads[count], _recordBuffer);
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

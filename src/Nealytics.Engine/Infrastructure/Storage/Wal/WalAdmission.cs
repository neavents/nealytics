namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

internal sealed class WalAdmission
{
    private readonly object _sync = new object();
    private readonly int _recordLimit;
    private readonly int _byteLimit;
    private TaskCompletionSource? _changed;
    private ExceptionDispatchInfo? _closed;
    private int _records;
    private int _bytes;

    public WalAdmission(int recordLimit, int byteLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recordLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(byteLimit, 1);
        _recordLimit = recordLimit;
        _byteLimit = byteLimit;
    }

    public int PendingRecords
    {
        get { lock (_sync) { return _records; } }
    }

    public int PendingBytes
    {
        get { lock (_sync) { return _bytes; } }
    }

    public ValueTask ReserveAsync(int bytes, CancellationToken cancellationToken) =>
        ReserveAsync(1, bytes, cancellationToken);

    public async ValueTask ReserveAsync(int records, int bytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(records, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(records, _recordLimit);
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, _byteLimit);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            lock (_sync)
            {
                _closed?.Throw();
                if (records <= _recordLimit - _records && bytes <= _byteLimit - _bytes)
                {
                    _records += records;
                    _bytes += bytes;
                    return;
                }

                _changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }

    public void Release(int bytes) => Release(1, bytes);

    public void Release(int records, int bytes)
    {
        TaskCompletionSource? changed;
        lock (_sync)
        {
            if (records < 1 || records > _records || bytes < 1 || bytes > _bytes)
            {
                throw new InvalidOperationException("WAL admission release exceeds the reserved budget.");
            }

            _records -= records;
            _bytes -= bytes;
            changed = _changed;
            _changed = null;
        }

        changed?.TrySetResult();
    }

    public void Close(Exception error)
    {
        lock (_sync)
        {
            _closed ??= ExceptionDispatchInfo.Capture(error);
            _changed?.TrySetResult();
        }
    }
}

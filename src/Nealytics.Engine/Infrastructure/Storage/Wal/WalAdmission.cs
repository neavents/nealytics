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

    public async ValueTask ReserveAsync(int bytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, _byteLimit);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            lock (_sync)
            {
                _closed?.Throw();
                if (_records < _recordLimit && bytes <= _byteLimit - _bytes)
                {
                    _records++;
                    _bytes += bytes;
                    return;
                }

                _changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }

    public void Release(int bytes)
    {
        TaskCompletionSource? changed;
        lock (_sync)
        {
            if (bytes < 1 || _records == 0 || bytes > _bytes)
            {
                throw new InvalidOperationException("WAL admission release exceeds the reserved budget.");
            }

            _records--;
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

namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

internal sealed class WalOperationGate
{
    private readonly object _sync = new object();
    private ExceptionDispatchInfo? _failure;
    private TaskCompletionSource? _idle;
    private int _active;
    private bool _stopped;

    public void Enter()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopped, typeof(WriteAheadLogger));
            _failure?.Throw();
            _active++;
        }
    }

    public void Exit()
    {
        lock (_sync)
        {
            _active--;
            if (_active == 0)
            {
                _idle?.TrySetResult();
            }
        }
    }

    public void ThrowIfFailed()
    {
        lock (_sync)
        {
            _failure?.Throw();
        }
    }

    public Exception Fail(Exception error)
    {
        lock (_sync)
        {
            _failure ??= ExceptionDispatchInfo.Capture(error);
            return _failure.SourceException;
        }
    }

    public Task Stop()
    {
        lock (_sync)
        {
            _stopped = true;
            if (_active == 0)
            {
                return Task.CompletedTask;
            }

            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task;
        }
    }
}

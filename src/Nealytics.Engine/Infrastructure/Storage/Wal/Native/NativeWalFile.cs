namespace Nealytics.Engine.Infrastructure.Storage.Wal.Native;

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

internal sealed class NativeWalFile : IWalFile
{
    private readonly object _sync = new object();
    private readonly INativeWalContext _context;
    private readonly INativeWalSignal _signal;
    private readonly TaskCompletionSource _exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private WorkRequest? _work;
    private ExceptionDispatchInfo? _failure;
    private Task? _disposal;
    private bool _stopping;

    public NativeWalFile(INativeWalContext context, INativeWalSignal? signal = null)
    {
        _context = context;
        _signal = signal ?? new NativeWalSignal();
        try
        {
            IsFileRegistered = context.IsFileRegistered;
            Thread worker = new Thread(RunCompletionLoop)
            {
                IsBackground = true,
                Name = "nealytics-wal-io"
            };
            worker.Start();
        }
        catch
        {
            _signal.Dispose();
            _context.Dispose();
            throw;
        }
    }

    public bool IsFileRegistered { get; }

    public ValueTask AppendAsync(ReadOnlyMemory<byte> record)
    {
        lock (_sync)
        {
            EnsureAvailable();
            try
            {
                _context.Append(record);
            }
            catch (Exception error)
            {
                _failure = ExceptionDispatchInfo.Capture(error);
                throw;
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync() => QueueWork(truncate: false);

    public ValueTask TruncateAsync() => QueueWork(truncate: true);

    private ValueTask QueueWork(bool truncate)
    {
        lock (_sync)
        {
            EnsureAvailable();
            WorkRequest request = new WorkRequest(truncate);
            _work = request;
            _signal.Pulse();
            return new ValueTask(request.Completion.Task);
        }
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        _failure?.Throw();
        if (_work is not null)
        {
            throw new InvalidOperationException("A native WAL file operation is already in progress.");
        }
    }

    private void RunCompletionLoop()
    {
        try
        {
            while (true)
            {
                WorkRequest? request;
                bool stopping;
                lock (_sync)
                {
                    request = _work;
                    stopping = _stopping;
                }

                if (request is not null)
                {
                    CompleteWork(request);
                }
                else if (stopping)
                {
                    return;
                }
                else
                {
                    _signal.Wait(Timeout.Infinite);
                }
            }
        }
        finally
        {
            _exited.TrySetResult();
        }
    }

    private void CompleteWork(WorkRequest request)
    {
        Exception? failure = null;
        try
        {
            if (request.Truncate)
            {
                _context.Truncate();
            }
            else
            {
                _context.Flush();
            }
        }
        catch (Exception error)
        {
            failure = error;
        }

        lock (_sync)
        {
            _work = null;
            if (failure is not null)
            {
                _failure = ExceptionDispatchInfo.Capture(failure);
            }
        }
        if (failure is null)
        {
            request.Completion.TrySetResult();
        }
        else
        {
            request.Completion.TrySetException(failure);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposal ??= DisposeCoreAsync();
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _stopping = true;
        _signal.Pulse();
        await _exited.Task;
        try
        {
            _context.Dispose();
        }
        finally
        {
            _signal.Dispose();
        }
    }

    private sealed class WorkRequest
    {
        public WorkRequest(bool truncate)
        {
            Truncate = truncate;
        }

        public bool Truncate { get; }
        public TaskCompletionSource Completion { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

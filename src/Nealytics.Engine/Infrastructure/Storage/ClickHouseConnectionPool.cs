namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Octonica.ClickHouseClient;

internal sealed class ClickHouseConnectionPool : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly int _idleCapacity;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _maxIdle;
    private readonly SemaphoreSlim _gate;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly Queue<IdleConnection> _idle = new Queue<IdleConnection>();
    private readonly object _sync = new object();
    private readonly object _disposeSync = new object();
    private Task? _disposeTask;
    private bool _disposed;

    private readonly record struct IdleConnection(ClickHouseConnection Connection, long Since);

    public ClickHouseConnectionPool(string connectionString, int capacity, int idleCapacity, TimeSpan timeout, TimeSpan maxIdle)
    {
        _connectionString = connectionString;
        _idleCapacity = idleCapacity;
        _timeout = timeout;
        _maxIdle = maxIdle;
        _gate = new SemaphoreSlim(capacity, capacity);
    }

    internal bool IsStale(long idleSinceTicks, long nowTicks) =>
        _maxIdle != Timeout.InfiniteTimeSpan && Stopwatch.GetElapsedTime(idleSinceTicks, nowTicks) >= _maxIdle;

    public async Task<PooledClickHouseConnection> AcquireAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        deadline.CancelAfter(_timeout);
        ClickHouseConnection? connection = null;
        bool acquired = false;
        bool leased = false;
        try
        {
            await _gate.WaitAsync(deadline.Token);
            acquired = true;
            while (true)
            {
                IdleConnection idle;
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!_idle.TryDequeue(out idle))
                    {
                        break;
                    }
                }
                connection = idle.Connection;
                if (connection.State == ConnectionState.Open && !IsStale(idle.Since, Stopwatch.GetTimestamp()))
                {
                    break;
                }
                await connection.DisposeAsync();
                connection = null;
            }
            if (connection is null)
            {
                connection = new ClickHouseConnection(_connectionString);
                await connection.OpenAsync(deadline.Token);
            }
            deadline.Token.ThrowIfCancellationRequested();
            PooledClickHouseConnection result = new PooledClickHouseConnection(connection, this);
            leased = true;
            return result;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            if (_shutdown.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(ClickHouseConnectionPool), error);
            }
            throw new TimeoutException("Timed out acquiring a ClickHouse workload connection.", error);
        }
        finally
        {
            if (!leased)
            {
                try
                {
                    if (connection is not null)
                    {
                        await connection.DisposeAsync();
                    }
                }
                finally
                {
                    if (acquired)
                    {
                        Release();
                    }
                }
            }
        }
    }

    public int EvictIdle()
    {
        int count = 0;
        lock (_sync)
        {
            while (_idle.TryDequeue(out IdleConnection idle))
            {
                idle.Connection.Dispose();
                count++;
            }
        }
        return count;
    }

    internal void Return(ClickHouseConnection connection, bool discard)
    {
        try
        {
            lock (_sync)
            {
                if (!discard && !_disposed && connection.State == ConnectionState.Open && _idle.Count < _idleCapacity)
                {
                    _idle.Enqueue(new IdleConnection(connection, Stopwatch.GetTimestamp()));
                    return;
                }
            }
            connection.Dispose();
        }
        finally
        {
            Release();
        }
    }

    private void Release()
    {
        try
        {
            _gate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        lock (_sync)
        {
            _disposed = true;
        }
        _shutdown.Cancel();
        try
        {
            List<ClickHouseConnection> idle = TakeIdle();
            Task[] shutdowns = new Task[idle.Count];
            for (int index = 0; index < idle.Count; index++)
            {
                shutdowns[index] = idle[index].DisposeAsync().AsTask();
            }
            await Task.WhenAll(shutdowns);
        }
        finally
        {
            _gate.Dispose();
            _shutdown.Dispose();
        }
    }

    private List<ClickHouseConnection> TakeIdle()
    {
        List<ClickHouseConnection> result = new List<ClickHouseConnection>();
        lock (_sync)
        {
            while (_idle.TryDequeue(out IdleConnection idle))
            {
                result.Add(idle.Connection);
            }
        }
        return result;
    }
}

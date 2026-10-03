namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Octonica.ClickHouseClient;

public sealed class ClickHouseConnectionFactory : IAsyncDisposable
{
    private readonly TelemetryEngineOptions _options;
    private readonly string _connectionString;
    private readonly SemaphoreSlim? _acquireGate;
    private readonly ConcurrentQueue<IdleConnection> _idleConnections;
    private readonly TimeSpan _maxIdle;
    private int _totalCreated;
    private volatile bool _disposed;

    private readonly record struct IdleConnection(ClickHouseConnection Connection, long IdleSinceTicks);

    public ClickHouseConnectionFactory(IOptions<TelemetryEngineOptions> options)
    {
        _options = options.Value;
        _connectionString = BuildConnectionString(_options);
        _acquireGate = _options.ConnectionPoolSize > 0
            ? new SemaphoreSlim(_options.ConnectionPoolSize, _options.ConnectionPoolSize)
            : null;
        _idleConnections = new ConcurrentQueue<IdleConnection>();
        _maxIdle = _options.ConnectionMaxIdleSeconds > 0
            ? TimeSpan.FromSeconds(_options.ConnectionMaxIdleSeconds)
            : Timeout.InfiniteTimeSpan;
    }

    internal bool IsStale(long idleSinceTicks, long nowTicks) =>
        _maxIdle != Timeout.InfiniteTimeSpan
        && Stopwatch.GetElapsedTime(idleSinceTicks, nowTicks) >= _maxIdle;

    private static string BuildConnectionString(TelemetryEngineOptions options)
    {
        ClickHouseConnectionStringBuilder builder =
            new ClickHouseConnectionStringBuilder(options.ClickHouseConnectionString)
            {
                Compress = options.EnableWireCompression
            };
        return builder.ToString();
    }

    public async Task<PooledClickHouseConnection> AcquireAsync(CancellationToken cancellationToken)
    {
        if (_acquireGate is not null)
        {
            await _acquireGate.WaitAsync(cancellationToken);
        }

        try
        {
            long now = Stopwatch.GetTimestamp();

            while (_idleConnections.TryDequeue(out IdleConnection pooled))
            {
                if (pooled.Connection.State == ConnectionState.Open
                    && !IsStale(pooled.IdleSinceTicks, now))
                {
                    return new PooledClickHouseConnection(pooled.Connection, this);
                }

                Interlocked.Decrement(ref _totalCreated);
                pooled.Connection.Dispose();
            }

            ClickHouseConnection connection = new ClickHouseConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            Interlocked.Increment(ref _totalCreated);
            return new PooledClickHouseConnection(connection, this);
        }
        catch
        {
            ReleaseGate();
            throw;
        }
    }

    internal void Evict(ClickHouseConnection connection)
    {
        try
        {
            Interlocked.Decrement(ref _totalCreated);
            connection.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            ReleaseGate();
        }
    }

    internal void Return(ClickHouseConnection connection)
    {
        try
        {
            if (_disposed || connection.State != ConnectionState.Open
                || Volatile.Read(ref _totalCreated) > _options.ConnectionPoolSize)
            {
                Interlocked.Decrement(ref _totalCreated);
                connection.Dispose();
                return;
            }

            _idleConnections.Enqueue(new IdleConnection(connection, Stopwatch.GetTimestamp()));
        }
        finally
        {
            ReleaseGate();
        }
    }

    private void ReleaseGate()
    {
        if (_acquireGate is null)
        {
            return;
        }

        try
        {
            _acquireGate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        while (_idleConnections.TryDequeue(out IdleConnection idle))
        {
            await idle.Connection.DisposeAsync();
        }

        _acquireGate?.Dispose();
    }
}

public readonly struct PooledClickHouseConnection : IAsyncDisposable
{
    private readonly ClickHouseConnection _connection;
    private readonly ClickHouseConnectionFactory _factory;
    private readonly StrongBox<bool> _discarded;

    internal PooledClickHouseConnection(ClickHouseConnection connection, ClickHouseConnectionFactory factory)
    {
        _connection = connection;
        _factory = factory;
        _discarded = new StrongBox<bool>(false);
    }

    public ClickHouseConnection Connection => _connection;

    public void Discard() => _discarded.Value = true;

    public ValueTask DisposeAsync()
    {
        if (_discarded.Value)
        {
            _factory.Evict(_connection);
        }
        else
        {
            _factory.Return(_connection);
        }

        return ValueTask.CompletedTask;
    }
}

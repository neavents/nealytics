namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Collections.Concurrent;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Octonica.ClickHouseClient;

public sealed class ClickHouseConnectionFactory : IAsyncDisposable
{
    private readonly TelemetryEngineOptions _options;
    private readonly ConcurrentQueue<ClickHouseConnection> _idleConnections;
    private int _totalCreated;
    private volatile bool _disposed;

    public ClickHouseConnectionFactory(IOptions<TelemetryEngineOptions> options)
    {
        _options = options.Value;
        _idleConnections = new ConcurrentQueue<ClickHouseConnection>();
    }

    public async Task<PooledClickHouseConnection> AcquireAsync(CancellationToken cancellationToken)
    {
        while (_idleConnections.TryDequeue(out ClickHouseConnection? pooled))
        {
            if (pooled.State == ConnectionState.Open)
            {
                return new PooledClickHouseConnection(pooled, this);
            }

            Interlocked.Decrement(ref _totalCreated);
            pooled.Dispose();
        }

        ClickHouseConnection connection = new ClickHouseConnection(_options.ClickHouseConnectionString);
        await connection.OpenAsync(cancellationToken);
        Interlocked.Increment(ref _totalCreated);
        return new PooledClickHouseConnection(connection, this);
    }

    internal void Return(ClickHouseConnection connection)
    {
        if (_disposed || connection.State != ConnectionState.Open
            || Volatile.Read(ref _totalCreated) > _options.ConnectionPoolSize)
        {
            Interlocked.Decrement(ref _totalCreated);
            connection.Dispose();
            return;
        }

        _idleConnections.Enqueue(connection);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        while (_idleConnections.TryDequeue(out ClickHouseConnection? connection))
        {
            await connection.DisposeAsync();
        }
    }
}

public readonly struct PooledClickHouseConnection : IAsyncDisposable
{
    private readonly ClickHouseConnection _connection;
    private readonly ClickHouseConnectionFactory _factory;

    internal PooledClickHouseConnection(ClickHouseConnection connection, ClickHouseConnectionFactory factory)
    {
        _connection = connection;
        _factory = factory;
    }

    public ClickHouseConnection Connection => _connection;

    public ValueTask DisposeAsync()
    {
        _factory.Return(_connection);
        return ValueTask.CompletedTask;
    }
}

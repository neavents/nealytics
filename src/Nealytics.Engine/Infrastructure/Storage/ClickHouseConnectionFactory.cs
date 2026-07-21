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
    private readonly string _connectionString;
    private readonly SemaphoreSlim? _acquireGate;
    private readonly ConcurrentQueue<ClickHouseConnection> _idleConnections;
    private int _totalCreated;
    private volatile bool _disposed;

    public ClickHouseConnectionFactory(IOptions<TelemetryEngineOptions> options)
    {
        _options = options.Value;
        _connectionString = BuildConnectionString(_options);
        _acquireGate = _options.ConnectionPoolSize > 0
            ? new SemaphoreSlim(_options.ConnectionPoolSize, _options.ConnectionPoolSize)
            : null;
        _idleConnections = new ConcurrentQueue<ClickHouseConnection>();
    }

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
            while (_idleConnections.TryDequeue(out ClickHouseConnection? pooled))
            {
                if (pooled.State == ConnectionState.Open)
                {
                    return new PooledClickHouseConnection(pooled, this);
                }

                Interlocked.Decrement(ref _totalCreated);
                pooled.Dispose();
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

            _idleConnections.Enqueue(connection);
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
        while (_idleConnections.TryDequeue(out ClickHouseConnection? connection))
        {
            await connection.DisposeAsync();
        }

        _acquireGate?.Dispose();
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

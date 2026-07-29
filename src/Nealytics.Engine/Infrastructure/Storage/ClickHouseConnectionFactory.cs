namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Collections.Concurrent;
using System.Data;
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

    /// <summary>
    /// Throws a connection away instead of pooling it, and frees its slot.
    /// </summary>
    /// <remarks>
    /// Used when a command failed on it. Deliberately does not inspect
    /// <see cref="ConnectionState"/> — the whole reason this exists is that the state lies about a
    /// connection whose server went away and came back.
    /// </remarks>
    internal void Evict(ClickHouseConnection connection)
    {
        try
        {
            Interlocked.Decrement(ref _totalCreated);
            connection.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already gone; nothing to do but release the slot below.
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

/// <summary>
/// A borrowed connection, returned to the pool on disposal unless it was discarded.
/// </summary>
/// <remarks>
/// <para>
/// The discard path exists because <see cref="ConnectionState.Open"/> is not the same as usable.
/// When ClickHouse restarts or goes read-only, connections opened before it went away keep
/// reporting Open and only reveal themselves on the next command — "Reached an unexpected end of
/// the server's response". <see cref="ClickHouseConnectionFactory.Return"/> checks the state, sees
/// Open, and puts the corpse back.
/// </para>
/// <para>
/// The observed effect: ClickHouse recovered the moment its disk was freed and accepted direct
/// inserts immediately, while nealytics kept failing every retry — each one drawing another dead
/// connection from a pool of nothing else. It took a restart. A caller that saw a command fail now
/// discards the connection, so the pool refills with live ones on its own.
/// </para>
/// <para>
/// A struct, so this holds a mutable flag by reference rather than by value — a discarded copy
/// must be the same discard the disposal sees.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Marks this connection as not worth reusing. Call it when a command failed for any reason
    /// other than cancellation — the cost of throwing away a healthy connection is one reconnect;
    /// the cost of keeping a dead one is every subsequent batch.
    /// </summary>
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

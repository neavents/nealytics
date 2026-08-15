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

    /// <summary>
    /// A pooled connection and the moment it went idle.
    ///
    /// <para><b>Why the timestamp is the fix.</b> ClickHouse closes a connection that has been idle
    /// past <c>idle_connection_timeout</c> — 3600 seconds by default — and logs nothing, because
    /// closing an idle socket is not an error. The client keeps reporting <c>Open</c>, so
    /// <see cref="ClickHouseConnectionFactory.AcquireAsync"/> could not tell a live connection from
    /// a corpse and had to find out by failing a write.</para>
    ///
    /// <para><b>Discarding on failure was not enough.</b> That path exists and works, but it costs
    /// one failed insert per dead connection. Measured on this estate: a pool of 16, five retries a
    /// batch, and cross-batch backoff meant fifteen consecutive failures over four minutes without
    /// draining the pool — indistinguishable from a hard outage, while <c>/health</c> and
    /// <c>/ready</c> stayed green because <c>SELECT</c> opens its own path and only the columnar
    /// writer breaks.</para>
    /// </summary>
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

    /// <summary>
    /// Whether a connection idle since <paramref name="idleSinceTicks"/> is too old to trust.
    /// Internal so the rule can be tested without a server.
    /// </summary>
    /// <remarks>
    /// <c>Stopwatch.GetElapsedTime</c>, not a raw tick subtraction. A <c>Stopwatch</c> tick is
    /// <c>1 / Stopwatch.Frequency</c> of a second — a nanosecond on Linux — while a
    /// <c>TimeSpan</c> tick is 100ns. Comparing one against the other made every connection look
    /// a hundred times older than it was, which recycles the pool on every acquire and turns it
    /// into a connect-per-batch loop.
    /// </remarks>
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
                // Age first. State is checked second because it cannot answer this question: a
                // connection the server closed an hour ago still reports Open, which is exactly how
                // a corpse used to be handed out and only reveal itself on the write.
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

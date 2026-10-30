namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Octonica.ClickHouseClient;

public enum ClickHouseWorkload
{
    Read,
    Write,
    Probe,
    Maintenance
}

public sealed record ClickHouseWorkloadMetadata(ClickHouseWorkload Workload);

public sealed class ClickHouseConnectionFactory : IAsyncDisposable
{
    private readonly ClickHouseConnectionPool[] _pools;

    public ClickHouseConnectionFactory(IOptions<TelemetryEngineOptions> options)
        : this(options, null, null)
    {
    }

    internal ClickHouseConnectionFactory(
        IOptions<TelemetryEngineOptions> options,
        Func<ClickHouseConnection, CancellationToken, Task>? openAsync,
        Func<ClickHouseConnection, ValueTask>? disposeConnectionAsync)
    {
        TelemetryEngineOptions configuration = options.Value;
        ArgumentOutOfRangeException.ThrowIfNegative(configuration.ConnectionPoolSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(configuration.UnpooledConnectionLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(configuration.WriteConnectionPoolSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(configuration.ProbeConnectionPoolSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(configuration.MaintenanceConnectionPoolSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(configuration.ConnectionAcquireTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(configuration.ProbeTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(configuration.ConnectionAcquireTimeoutSeconds, 4_294_967);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(configuration.ProbeTimeoutSeconds, 4_294_967);
        ClickHouseConnectionStringBuilder builder = new ClickHouseConnectionStringBuilder(configuration.ClickHouseConnectionString)
        {
            Compress = configuration.EnableWireCompression
        };
        string connectionString = builder.ToString();
        TimeSpan timeout = TimeSpan.FromSeconds(configuration.ConnectionAcquireTimeoutSeconds);
        TimeSpan maxIdle = configuration.ConnectionMaxIdleSeconds > 0
            ? TimeSpan.FromSeconds(configuration.ConnectionMaxIdleSeconds) : Timeout.InfiniteTimeSpan;
        int reads = configuration.ConnectionPoolSize == 0
            ? configuration.UnpooledConnectionLimit : configuration.ConnectionPoolSize;
        _pools =
        [
            new ClickHouseConnectionPool(connectionString, reads, configuration.ConnectionPoolSize,
                timeout, maxIdle, openAsync, disposeConnectionAsync),
            new ClickHouseConnectionPool(connectionString, configuration.WriteConnectionPoolSize,
                configuration.WriteConnectionPoolSize, timeout, maxIdle, openAsync, disposeConnectionAsync),
            new ClickHouseConnectionPool(connectionString, configuration.ProbeConnectionPoolSize,
                configuration.ProbeConnectionPoolSize, timeout, maxIdle, openAsync, disposeConnectionAsync),
            new ClickHouseConnectionPool(connectionString, configuration.MaintenanceConnectionPoolSize,
                configuration.MaintenanceConnectionPoolSize, timeout, maxIdle, openAsync, disposeConnectionAsync)
        ];
        ProbeTimeout = TimeSpan.FromSeconds(configuration.ProbeTimeoutSeconds);
        MaximumConcurrentConnections = (long)reads + configuration.WriteConnectionPoolSize
            + configuration.ProbeConnectionPoolSize + configuration.MaintenanceConnectionPoolSize;
    }

    public long MaximumConcurrentConnections { get; }
    internal TimeSpan ProbeTimeout { get; }

    internal bool IsStale(long idleSinceTicks, long nowTicks) => _pools[0].IsStale(idleSinceTicks, nowTicks);

    public Task<PooledClickHouseConnection> AcquireAsync(CancellationToken cancellationToken) =>
        AcquireAsync(ClickHouseWorkload.Read, cancellationToken);

    public Task<PooledClickHouseConnection> AcquireAsync(ClickHouseWorkload workload, CancellationToken cancellationToken) =>
        Pool(workload).AcquireAsync(cancellationToken);

    public int EvictIdle() => EvictIdle(ClickHouseWorkload.Read);

    public int EvictIdle(ClickHouseWorkload workload) => Pool(workload).EvictIdle();

    public async ValueTask DisposeAsync()
    {
        Task[] shutdowns = new Task[_pools.Length];
        for (int index = 0; index < _pools.Length; index++)
        {
            shutdowns[index] = _pools[index].DisposeAsync().AsTask();
        }
        await Task.WhenAll(shutdowns);
    }

    private ClickHouseConnectionPool Pool(ClickHouseWorkload workload)
    {
        ArgumentOutOfRangeException.ThrowIfNegative((int)workload);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((int)workload, _pools.Length);
        return _pools[(int)workload];
    }
}

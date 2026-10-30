using System.Data;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Integration;

public sealed class ClickHouseWorkloadCapacityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryWorkloadUsesTheConfiguredWireCompression(bool compress)
    {
        var options = Options();
        options.Value.EnableWireCompression = compress;
        await using ClickHouseConnectionFactory factory = new(options);
        foreach (ClickHouseWorkload workload in Enum.GetValues<ClickHouseWorkload>())
        {
            await using PooledClickHouseConnection lease = await factory.AcquireAsync(workload, CancellationToken.None);
            Assert.Equal(compress, new ClickHouseConnectionStringBuilder(lease.Connection.ConnectionString).Compress);
            await using ClickHouseCommand command = lease.Connection.CreateCommand("SELECT sum(number) FROM numbers(10000)");
            Assert.Equal((ulong)49_995_000, await command.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task AcquisitionDeadlineAlsoBoundsAConnectionWhoseServerNeverFinishesTheHandshake()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        IOptions<TelemetryEngineOptions> options = Options(timeout: 1);
        options.Value.ClickHouseConnectionString = $"Host=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};Database=nealytics_core;User=default;Password=;";
        await using ClickHouseConnectionFactory factory = new(options);
        Task<PooledClickHouseConnection> acquisition = factory.AcquireAsync(CancellationToken.None);
        using TcpClient accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(() => acquisition.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Equal("Timed out acquiring a ClickHouse workload connection.", error.Message);
    }

    [Fact]
    public async Task SaturatedReadsCannotBorrowWriteProbeOrMaintenanceReservations()
    {
        await using ClickHouseConnectionFactory factory = new(Options());
        await using PooledClickHouseConnection read = await factory.AcquireAsync(CancellationToken.None);
        using CancellationTokenSource canceled = new();
        Task<PooledClickHouseConnection> waiting = factory.AcquireAsync(canceled.Token);
        foreach (ClickHouseWorkload workload in new[] { ClickHouseWorkload.Write, ClickHouseWorkload.Probe, ClickHouseWorkload.Maintenance })
        {
            await using PooledClickHouseConnection lease = await factory.AcquireAsync(workload, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await using ClickHouseCommand command = lease.Connection.CreateCommand();
            command.CommandText = "SELECT 1";
            Assert.Equal((byte)1, await command.ExecuteScalarAsync());
            Assert.NotSame(read.Connection, lease.Connection);
        }
        Assert.False(waiting.IsCompleted);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(4, factory.MaximumConcurrentConnections);
    }

    [Fact]
    public async Task RealBatchIngestionProgressesWhileTheReadPoolIsFullyBorrowed()
    {
        IOptions<TelemetryEngineOptions> options = Options();
        await using ClickHouseConnectionFactory factory = new(options);
        await using PooledClickHouseConnection occupied = await factory.AcquireAsync(CancellationToken.None);
        DimensionRegistry dimensions = new(options.Value);
        ClickHouseBatchWriter writer = new(factory, dimensions, new MeasureRegistry(options.Value, dimensions),
            options, NullLogger<ClickHouseBatchWriter>.Instance);
        GlobalTelemetryPayload payload = new()
        {
            ProjectId = "capacity-test", TenantId = Guid.NewGuid().ToString("N"), SessionId = "s", EventType = "e"
        };
        await writer.WriteAsync([payload], 1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await using ClickHouseCommand query = occupied.Connection.CreateCommand();
        query.CommandText = $"SELECT count() FROM nealytics_core.global_events WHERE event_id = '{payload.EventId}'";
        Assert.Equal((ulong)1, await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AcquisitionDeadlineIncludesWaitingForAReservationAndDoesNotLeakThePermit()
    {
        await using ClickHouseConnectionFactory factory = new(Options(timeout: 1));
        PooledClickHouseConnection occupied = await factory.AcquireAsync(CancellationToken.None);
        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(() => factory.AcquireAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Equal("Timed out acquiring a ClickHouse workload connection.", error.Message);
        await occupied.DisposeAsync();
        await using PooledClickHouseConnection next = await factory.AcquireAsync(CancellationToken.None);
        Assert.Same(occupied.Connection, next.Connection);
    }

    [Fact]
    public async Task DisposalWakesWaitersAndOutstandingLeasesCloseOnReturn()
    {
        ClickHouseConnectionFactory factory = new(Options());
        PooledClickHouseConnection occupied = await factory.AcquireAsync(CancellationToken.None);
        Task<PooledClickHouseConnection> waiting = factory.AcquireAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        await factory.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        await occupied.DisposeAsync();
        Assert.NotEqual(ConnectionState.Open, occupied.Connection.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.AcquireAsync(CancellationToken.None));
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task CopiedLeasesCannotReturnTheSameConnectionTwice()
    {
        await using ClickHouseConnectionFactory factory = new(Options(reads: 2));
        PooledClickHouseConnection original = await factory.AcquireAsync(CancellationToken.None);
        PooledClickHouseConnection copy = original;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => copy.DisposeAsync().AsTask()));
        await original.DisposeAsync();
        await using PooledClickHouseConnection first = await factory.AcquireAsync(CancellationToken.None);
        await using PooledClickHouseConnection second = await factory.AcquireAsync(CancellationToken.None);
        Assert.Same(original.Connection, first.Connection);
        Assert.NotSame(first.Connection, second.Connection);
    }

    [Fact]
    public async Task DiscardAcrossCopiesClosesTheConnectionAndPreservesCapacity()
    {
        await using ClickHouseConnectionFactory factory = new(Options());
        PooledClickHouseConnection original = await factory.AcquireAsync(CancellationToken.None);
        PooledClickHouseConnection copy = original;
        copy.Discard();
        await original.DisposeAsync();
        await copy.DisposeAsync();
        Assert.NotEqual(ConnectionState.Open, original.Connection.State);
        await using PooledClickHouseConnection next = await factory.AcquireAsync(CancellationToken.None);
        Assert.NotSame(original.Connection, next.Connection);
    }

    [Fact]
    public async Task IdleEvictionIsConfinedToTheSelectedWorkload()
    {
        await using ClickHouseConnectionFactory factory = new(Options());
        Dictionary<ClickHouseWorkload, ClickHouseConnection> idle = [];
        foreach (ClickHouseWorkload workload in Enum.GetValues<ClickHouseWorkload>())
        {
            await using PooledClickHouseConnection lease = await factory.AcquireAsync(workload, CancellationToken.None);
            idle[workload] = lease.Connection;
        }
        Assert.Equal(1, factory.EvictIdle());
        Assert.NotEqual(ConnectionState.Open, idle[ClickHouseWorkload.Read].State);
        foreach (ClickHouseWorkload workload in Enum.GetValues<ClickHouseWorkload>())
        {
            await using PooledClickHouseConnection lease = await factory.AcquireAsync(workload, CancellationToken.None);
            Assert.Equal(workload != ClickHouseWorkload.Read, ReferenceEquals(idle[workload], lease.Connection));
        }
    }

    [Fact]
    public async Task FailedOpensReleaseTheirReservationForTheNextAttempt()
    {
        IOptions<TelemetryEngineOptions> options = Options();
        options.Value.ClickHouseConnectionString = "Host=127.0.0.1;Port=9;Database=nealytics_core;User=default;Password=;";
        await using ClickHouseConnectionFactory factory = new(options);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Exception error = await Assert.ThrowsAnyAsync<Exception>(() => factory.AcquireAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(error is TimeoutException, error.ToString());
        }
    }

    private static IOptions<TelemetryEngineOptions> Options(int reads = 1, int timeout = 5) =>
        Microsoft.Extensions.Options.Options.Create(new TelemetryEngineOptions
        {
            ClickHouseConnectionString = ClickHouseTestSupport.ConnectionString,
            ConnectionPoolSize = reads,
            ConnectionAcquireTimeoutSeconds = timeout
        });
}

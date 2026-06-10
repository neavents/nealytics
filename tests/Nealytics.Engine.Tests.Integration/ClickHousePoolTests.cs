using System.Data;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;
using NSubstitute;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Integration;

public class ClickHousePoolTests
{
    private static IOptions<TelemetryEngineOptions> CreateOptions(int poolSize = 8)
    {
        var opts = new TelemetryEngineOptions
        {
            ClickHouseConnectionString = "Host=127.0.0.1;Port=9100;Database=nealytics_core;User=default;Password=;",
            ConnectionPoolSize = poolSize
        };
        var mock = Substitute.For<IOptions<TelemetryEngineOptions>>();
        mock.Value.Returns(opts);
        return mock;
    }

    [Fact]
    public async Task AcquireAsync_CreatesNewConnection_WhenPoolIsEmpty()
    {
        await using var factory = new ClickHouseConnectionFactory(CreateOptions());
        await using var lease = await factory.AcquireAsync(CancellationToken.None);
        lease.Connection.State.Should().Be(ConnectionState.Open);
    }

    [Fact]
    public async Task AcquireAsync_ReusesIdleConnection_AfterReturn()
    {
        await using var factory = new ClickHouseConnectionFactory(CreateOptions());

        ClickHouseConnection firstConnection;
        {
            await using var lease = await factory.AcquireAsync(CancellationToken.None);
            firstConnection = lease.Connection;
        }

        await using var lease2 = await factory.AcquireAsync(CancellationToken.None);
        lease2.Connection.Should().BeSameAs(firstConnection, "idle connection should be reused");
    }

    [Fact]
    public async Task AcquireAsync_SkipsStaleConnection_WhenStateIsClosed()
    {
        await using var factory = new ClickHouseConnectionFactory(CreateOptions());

        ClickHouseConnection firstConnection;
        {
            await using var lease = await factory.AcquireAsync(CancellationToken.None);
            firstConnection = lease.Connection;
        }

        firstConnection.Close();

        await using var lease2 = await factory.AcquireAsync(CancellationToken.None);
        lease2.Connection.Should().NotBeSameAs(firstConnection, "stale connection should be discarded");
        lease2.Connection.State.Should().Be(ConnectionState.Open);
    }

    [Fact]
    public async Task Return_DisposesConnection_WhenPoolIsFull()
    {
        var options = CreateOptions(poolSize: 0);
        await using var factory = new ClickHouseConnectionFactory(options);

        await using var lease = await factory.AcquireAsync(CancellationToken.None);
        var conn = lease.Connection;

        await lease.DisposeAsync();
        conn.State.Should().NotBe(ConnectionState.Open);
    }

    [Fact]
    public async Task Return_DisposesConnection_WhenFactoryIsDisposed()
    {
        await using var factory = new ClickHouseConnectionFactory(CreateOptions());

        await using var lease = await factory.AcquireAsync(CancellationToken.None);
        var conn = lease.Connection;

        await factory.DisposeAsync();
        await lease.DisposeAsync();

        await lease.DisposeAsync();

        conn.State.Should().NotBe(ConnectionState.Open);
    }

    [Fact]
    public async Task DisposeAsync_DisposesAllIdleConnections()
    {
        var factory = new ClickHouseConnectionFactory(CreateOptions());

        var conns = new List<ClickHouseConnection>();
        for (int i = 0; i < 3; i++)
        {
            var lease = await factory.AcquireAsync(CancellationToken.None);
            conns.Add(lease.Connection);
            await lease.DisposeAsync();
        }

        await factory.DisposeAsync();

        foreach (var conn in conns)
        {
            conn.State.Should().NotBe(ConnectionState.Open);
        }
    }

    [Fact]
    public async Task AcquireAsync_MultipleConcurrent_AllGetOpenConnections()
    {
        await using var factory = new ClickHouseConnectionFactory(CreateOptions(16));

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var lease = await factory.AcquireAsync(CancellationToken.None);
            lease.Connection.State.Should().Be(ConnectionState.Open);
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task AcquireAsync_RespectsCancellationToken()
    {
        await using var factory = new ClickHouseConnectionFactory(CreateOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => factory.AcquireAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PooledLease_DisposeAsync_IsIdempotent()
    {
        await using var factory = new ClickHouseConnectionFactory(CreateOptions());

        var lease = await factory.AcquireAsync(CancellationToken.None);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
    }
}

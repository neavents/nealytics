using System.Data;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Integration;

public sealed class ClickHousePoolLifecycleIntegrationTests
{
    [Fact]
    public async Task ARejectedIdleConnectionFinishesCleanupBeforeItsReplacementIsIssued()
    {
        TaskCompletionSource closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClickHouseConnection? stale = null;
        await using ClickHouseConnectionFactory factory = new(Options.Create(Settings()), null, async connection =>
        {
            if (ReferenceEquals(connection, stale))
            {
                closing.TrySetResult();
                await release.Task;
            }
            await connection.DisposeAsync();
        });
        PooledClickHouseConnection first = await factory.AcquireAsync(CancellationToken.None);
        stale = first.Connection;
        await first.DisposeAsync();
        stale.Close();
        Task<PooledClickHouseConnection> replacement = factory.AcquireAsync(CancellationToken.None);
        try
        {
            Assert.Same(closing.Task, await Task.WhenAny(closing.Task, replacement).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(replacement.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await using PooledClickHouseConnection next = await replacement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(stale, next.Connection);
        Assert.Equal(ConnectionState.Open, next.Connection.State);
    }

    [Fact]
    public async Task FactoryDisposalAwaitsEveryWorkloadsPendingConnectionCleanup()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ClickHouseConnection> closed = [];
        ClickHouseConnectionFactory factory = new(Options.Create(Settings()), null, async connection =>
        {
            closed.Add(connection);
            await release.Task;
            await connection.DisposeAsync();
        });
        List<ClickHouseConnection> opened = [];
        foreach (ClickHouseWorkload workload in Enum.GetValues<ClickHouseWorkload>())
        {
            await using PooledClickHouseConnection lease = await factory.AcquireAsync(workload, CancellationToken.None);
            opened.Add(lease.Connection);
        }
        ValueTask first = factory.DisposeAsync();
        ValueTask second = factory.DisposeAsync();
        try
        {
            Assert.Equal(opened, closed);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(first.AsTask(), second.AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(opened, connection => Assert.NotEqual(ConnectionState.Open, connection.State));
    }

    [Fact]
    public async Task CanceledConnectionCleanupPreservesItsCancellationAndStillClosesThePool()
    {
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        ClickHouseConnectionFactory factory = new(Options.Create(Settings()), null,
            _ => ValueTask.FromCanceled(canceled.Token));
        PooledClickHouseConnection lease = await factory.AcquireAsync(CancellationToken.None);
        await lease.DisposeAsync();
        Task disposal = factory.DisposeAsync().AsTask();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(canceled.Token, error.CancellationToken);
            Assert.True(disposal.IsCanceled);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.AcquireAsync(CancellationToken.None));
        }
        finally
        {
            lease.Connection.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneFailingCleanupCannotSkipOrOutliveOtherIdleConnectionCleanup(bool asynchronousFailure)
    {
        TaskCompletionSource closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IOException failure = new("connection cleanup failed");
        List<ClickHouseConnection> attempted = [];
        ClickHouseConnection? failing = null;
        ClickHouseConnectionPool pool = new(ClickHouseTestSupport.ConnectionString, 2, 2,
            TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan, null, connection =>
            {
                attempted.Add(connection);
                if (ReferenceEquals(connection, failing))
                {
                    if (asynchronousFailure)
                    {
                        return ValueTask.FromException(failure);
                    }
                    throw failure;
                }
                return CloseAfterRelease(connection);
            });
        PooledClickHouseConnection first = await pool.AcquireAsync(CancellationToken.None);
        PooledClickHouseConnection second = await pool.AcquireAsync(CancellationToken.None);
        failing = first.Connection;
        await first.DisposeAsync();
        await second.DisposeAsync();
        ValueTask disposal = pool.DisposeAsync();
        try
        {
            Assert.Equal(new[] { first.Connection, second.Connection }, attempted);
            Assert.True(closing.Task.IsCompletedSuccessfully);
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            first.Connection.Dispose();
        }

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => disposal.AsTask().WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.NotEqual(ConnectionState.Open, second.Connection.State);

        async ValueTask CloseAfterRelease(ClickHouseConnection connection)
        {
            closing.TrySetResult();
            await release.Task;
            await connection.DisposeAsync();
        }
    }

    private static TelemetryEngineOptions Settings() => new()
    {
        ClickHouseConnectionString = ClickHouseTestSupport.ConnectionString,
        ConnectionPoolSize = 1
    };
}

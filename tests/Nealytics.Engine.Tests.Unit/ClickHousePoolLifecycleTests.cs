using System.Reflection;
using System.Threading.Channels;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Unit;

public sealed class ClickHousePoolLifecycleTests
{
    [Fact]
    public async Task ClosedPoolsReportTheirOwnIdentityBeforeAccessingDisposedSynchronization()
    {
        await using var pool = Pool();
        await pool.DisposeAsync();

        var error = await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.AcquireAsync(CancellationToken.None));

        Assert.Equal(typeof(ClickHouseConnectionPool).FullName, error.ObjectName);
    }

    [Fact]
    public async Task DisposalClosesTheOwnedSynchronizationHandles()
    {
        await using var pool = Pool();
        var gate = OwnedResource<SemaphoreSlim>(pool, "_gate");
        var shutdown = OwnedResource<CancellationTokenSource>(pool, "_shutdown");
        using WaitHandle gateHandle = gate.AvailableWaitHandle;
        using WaitHandle shutdownHandle = shutdown.Token.WaitHandle;

        await pool.DisposeAsync();

        Assert.True(gateHandle.SafeWaitHandle.IsClosed);
        Assert.True(shutdownHandle.SafeWaitHandle.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => gate.Wait(0));
        Assert.Throws<ObjectDisposedException>(() => shutdown.Token);
    }

    [Fact]
    public async Task CancellationAfterOpeningCannotIssueALeaseAndStillClosesTheConnection()
    {
        using CancellationTokenSource canceled = new();
        ClickHouseConnection? opened = null;
        ClickHouseConnection? closed = null;
        CancellationToken openingToken = default;
        await using var pool = Pool((connection, token) =>
        {
            opened = connection;
            openingToken = token;
            canceled.Cancel();
            return Task.CompletedTask;
        }, connection =>
        {
            closed = connection;
            return connection.DisposeAsync();
        });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.AcquireAsync(canceled.Token));

        Assert.Equal(openingToken, error.CancellationToken);
        Assert.NotNull(opened);
        Assert.Same(opened, closed);
    }

    [Fact]
    public async Task FailedOpeningWaitsForConnectionCleanupBeforeReturningItsReservation()
    {
        TaskCompletionSource closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IOException failure = new("opening failed");
        ClickHouseConnection? opened = null;
        await using var pool = Pool((connection, _) =>
        {
            opened = connection;
            return Task.FromException(failure);
        }, async connection =>
        {
            Assert.Same(opened, connection);
            closing.TrySetResult();
            await release.Task;
            await connection.DisposeAsync();
        });
        Task<PooledClickHouseConnection> acquisition = pool.AcquireAsync(CancellationToken.None);
        try
        {
            Assert.Same(closing.Task, await Task.WhenAny(acquisition, closing.Task).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(acquisition.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => acquisition));
    }

    [Fact]
    public async Task FailedConnectionCleanupStillReleasesTheAcquisitionReservation()
    {
        int openings = 0;
        IOException cleanup = new("cleanup failed");
        await using var pool = Pool((_, _) => Interlocked.Increment(ref openings) == 1
            ? Task.FromException(new IOException("opening failed")) : Task.CompletedTask,
            _ => ValueTask.FromException(cleanup));

        Assert.Same(cleanup, await Assert.ThrowsAsync<IOException>(() => pool.AcquireAsync(CancellationToken.None)));

        await using PooledClickHouseConnection next = await pool.AcquireAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, openings);
    }

    [Fact]
    public async Task DisposalAfterAReservationIsGrantedPreventsAnotherConnectionFromOpening()
    {
        int openings = 0;
        await using var pool = Pool((_, _) =>
        {
            Interlocked.Increment(ref openings);
            return Task.CompletedTask;
        });
        PooledClickHouseConnection occupied = await pool.AcquireAsync(CancellationToken.None);
        QueuedContext continuation = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task<PooledClickHouseConnection> waiting;
        try
        {
            SynchronizationContext.SetSynchronizationContext(continuation);
            waiting = pool.AcquireAsync(CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        await occupied.DisposeAsync();
        await continuation.Posted.WaitAsync(TimeSpan.FromSeconds(3));
        ValueTask disposal = pool.DisposeAsync();
        Assert.False(disposal.IsCompleted);

        await continuation.RunUntilAsync(waiting);

        var error = await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
        Assert.Equal(typeof(ClickHouseConnectionPool).FullName, error.ObjectName);
        Assert.Equal(1, openings);
        await disposal.AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ReentrantDisposalFromAnOpeningCancellationCallbackSharesTheExistingShutdown()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClickHouseConnectionPool? pool = null;
        ValueTask reentrant = default;
        CancellationTokenRegistration registration = default;
        pool = Pool((_, token) =>
        {
            registration = token.Register(() => reentrant = pool!.DisposeAsync());
            return release.Task;
        });
        Task<PooledClickHouseConnection> acquisition = pool.AcquireAsync(CancellationToken.None);
        ValueTask first = pool.DisposeAsync();
        try
        {
            Assert.Same(first.AsTask(), reentrant.AsTask());
            Assert.False(first.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            registration.Dispose();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => acquisition.WaitAsync(TimeSpan.FromSeconds(3)));
        await Task.WhenAll(first.AsTask(), reentrant.AsTask()).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AThrowingCancellationCallbackCannotSkipAcquisitionDrainOrSynchronizationCleanup()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InvalidOperationException failure = new("cancellation callback failed");
        CancellationTokenRegistration registration = default;
        var pool = Pool((_, token) =>
        {
            registration = token.Register(() => throw failure);
            return release.Task;
        });
        using WaitHandle gateHandle = OwnedResource<SemaphoreSlim>(pool, "_gate").AvailableWaitHandle;
        using WaitHandle shutdownHandle = OwnedResource<CancellationTokenSource>(pool, "_shutdown").Token.WaitHandle;
        Task<PooledClickHouseConnection> acquisition = pool.AcquireAsync(CancellationToken.None);
        ValueTask disposal = pool.DisposeAsync();
        try
        {
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            registration.Dispose();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => acquisition.WaitAsync(TimeSpan.FromSeconds(3)));
        var error = await Assert.ThrowsAsync<AggregateException>(() => disposal.AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Same(failure, Assert.Single(error.Flatten().InnerExceptions));
        Assert.True(gateHandle.SafeWaitHandle.IsClosed);
        Assert.True(shutdownHandle.SafeWaitHandle.IsClosed);
    }

    [Fact]
    public async Task ClosingDuringAnUncooperativeOpenWaitsForAcquisitionAndRejectsItsConnection()
    {
        TaskCompletionSource opening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClickHouseConnection? created = null;
        ClickHouseConnection? closed = null;
        await using var pool = Pool((connection, _) =>
        {
            created = connection;
            opening.TrySetResult();
            return release.Task;
        }, connection =>
        {
            closed = connection;
            return connection.DisposeAsync();
        });
        Task<PooledClickHouseConnection> acquisition = pool.AcquireAsync(CancellationToken.None);
        await opening.Task.WaitAsync(TimeSpan.FromSeconds(3));
        ValueTask first = pool.DisposeAsync();
        ValueTask second = pool.DisposeAsync();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => acquisition.WaitAsync(TimeSpan.FromSeconds(3)));
        await Task.WhenAll(first.AsTask(), second.AsTask()).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(created);
        Assert.Same(created, closed);
    }

    private static ClickHouseConnectionPool Pool(
        Func<ClickHouseConnection, CancellationToken, Task>? open = null,
        Func<ClickHouseConnection, ValueTask>? close = null) =>
        new("Host=127.0.0.1;Port=1;Database=default;User=default;Password=;", 1, 0,
            TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan, open, close);

    private static T OwnedResource<T>(ClickHouseConnectionPool pool, string name) =>
        (T)typeof(ClickHouseConnectionPool).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Channel<(SendOrPostCallback Callback, object? State)> _work =
            Channel.CreateUnbounded<(SendOrPostCallback, object?)>();
        private readonly TaskCompletionSource _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Posted => _posted.Task;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Assert.True(_work.Writer.TryWrite((callback, state)));
            _posted.TrySetResult();
        }

        public async Task RunUntilAsync(Task operation)
        {
            while (!operation.IsCompleted)
            {
                var work = await _work.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                work.Callback(work.State);
            }
        }
    }
}

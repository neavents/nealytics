using Nealytics.Engine.Infrastructure.Storage.Wal.Native;

namespace Nealytics.Engine.Tests.Unit;

public sealed class NativeWalFileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlushAndTruncateReuseOneCompletionOwner(bool registered)
    {
        using TestContext context = new() { Registered = registered };
        await using NativeWalFile file = new(context);
        Assert.Equal(registered, file.IsFileRegistered);
        byte[] record = [1, 2, 3];
        await file.AppendAsync(record);
        record.AsSpan().Fill(9);
        for (int index = 0; index < 5; index++)
        {
            await file.FlushAsync();
            await file.TruncateAsync();
        }

        Assert.Equal([1, 2, 3], Assert.Single(context.Records));
        Assert.Equal(5, context.FlushCalls);
        Assert.Equal(5, context.TruncateCalls);
        int owner = Assert.Single(context.CompletionThreads.Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, owner);
        Assert.False(context.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileCannotBeChangedWhileItsCompletionIsOutstanding(bool truncate)
    {
        using TestContext context = new() { Gated = true };
        await using NativeWalFile file = new(context);
        Task operation = (truncate ? file.TruncateAsync() : file.FlushAsync()).AsTask();
        try
        {
            await context.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => file.AppendAsync(new byte[1]).AsTask());
            Assert.Contains("operation is already in progress", error.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => file.FlushAsync().AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => file.TruncateAsync().AsTask());
        }
        finally
        {
            context.Release.Set();
        }

        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await file.AppendAsync(new byte[1]);
        Assert.Single(context.Records);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("flush")]
    [InlineData("truncate")]
    public async Task StorageFailureIsStickyAndNoLaterOperationReachesTheContext(string operation)
    {
        using TestContext context = new() { FailingOperation = operation };
        await using NativeWalFile file = new(context);
        Task write = operation switch
        {
            "append" => CaptureAppendAsync(file),
            "flush" => file.FlushAsync().AsTask(),
            _ => file.TruncateAsync().AsTask()
        };
        Assert.Same(context.Failure, await Assert.ThrowsAsync<IOException>(() => write));
        await Assert.ThrowsAsync<IOException>(() => file.AppendAsync(new byte[1]).AsTask());
        await Assert.ThrowsAsync<IOException>(() => file.FlushAsync().AsTask());
        await Assert.ThrowsAsync<IOException>(() => file.TruncateAsync().AsTask());
        Assert.Equal(1, context.AppendCalls + context.FlushCalls + context.TruncateCalls);
    }

    [Fact]
    public async Task DisposalWaitsForInFlightWorkAndClosesTheContextExactlyOnce()
    {
        using TestContext context = new() { Gated = true };
        NativeWalFile file = new(context);
        Task flush = file.FlushAsync().AsTask();
        await context.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task[] disposals = Enumerable.Range(0, 8).Select(_ => file.DisposeAsync().AsTask()).ToArray();
        try
        {
            Assert.All(disposals, disposal => Assert.Same(disposals[0], disposal));
            Assert.False(disposals[0].IsCompleted);
            Assert.False(context.Disposed);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => file.AppendAsync(new byte[1]).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => file.FlushAsync().AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => file.TruncateAsync().AsTask());
        }
        finally
        {
            context.Release.Set();
        }

        await Task.WhenAll(disposals.Append(flush)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(context.Disposed);
        Assert.Equal(1, context.DisposeCalls);
    }

    [Fact]
    public async Task WorkQueuedImmediatelyBeforeDisposalIsNeverLostToACoalescedSignal()
    {
        for (int index = 0; index < 30; index++)
        {
            using TestContext context = new();
            NativeWalFile file = new(context);
            Task flush = file.FlushAsync().AsTask();
            Task disposal = file.DisposeAsync().AsTask();
            await Task.WhenAll(flush, disposal).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, context.FlushCalls);
            Assert.Equal(1, context.DisposeCalls);
        }
    }

    [Fact]
    public async Task IdleDisposalAndRepeatedDisposalHaveOneOutcome()
    {
        using TestContext context = new();
        NativeWalFile file = new(context);
        Task disposal = file.DisposeAsync().AsTask();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(disposal, file.DisposeAsync().AsTask());
        Assert.Equal(1, context.DisposeCalls);
    }

    [Fact]
    public async Task DisposalFailureRemainsVisibleAndNewWorkIsRefused()
    {
        using TestContext context = new() { FailingOperation = "dispose" };
        TestSignal signal = new();
        NativeWalFile file = new(context, signal);
        Task disposal = file.DisposeAsync().AsTask();
        Assert.Same(context.Failure, await Assert.ThrowsAsync<IOException>(() => disposal));
        Assert.Same(disposal, file.DisposeAsync().AsTask());
        Assert.Equal(1, context.DisposeCalls);
        Assert.True(signal.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => file.FlushAsync().AsTask());
    }

    [Fact]
    public void ConstructorFailureClosesTheTransferredContext()
    {
        using TestContext context = new() { FailingOperation = "registration" };
        TestSignal signal = new();
        Assert.Same(context.Failure, Assert.Throws<IOException>(() => new NativeWalFile(context, signal)));
        Assert.Equal(1, context.DisposeCalls);
        Assert.True(signal.Disposed);
    }

    [Fact]
    public async Task CompletionOwnerSleepsWithoutPollingAndReleasesItsWakeSignal()
    {
        using TestContext context = new();
        TestSignal signal = new();
        NativeWalFile file = new(context, signal);
        try
        {
            Thread owner = await signal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(owner.IsBackground);
            Assert.Equal("nealytics-wal-io", owner.Name);
            Assert.Equal(Timeout.Infinite, signal.Timeout);
            Assert.False(signal.Disposed);
            Assert.Equal(0, signal.Pulses);
        }
        finally
        {
            await file.DisposeAsync();
        }
        Assert.True(signal.Disposed);
        Assert.Equal(1, signal.Pulses);
        Assert.Equal(1, context.DisposeCalls);
    }

    [Fact]
    public void WakeSignalStartsUnsignaledAndConsumesExactlyOnePulse()
    {
        NativeWalSignal signal = new();
        try
        {
            Assert.False(signal.Wait(0));
            signal.Pulse();
            Assert.True(signal.Wait(0));
            Assert.False(signal.Wait(0));
        }
        finally
        {
            signal.Dispose();
        }
        Assert.Throws<ObjectDisposedException>(() => signal.Wait(0));
    }

    [Fact]
    public void UnsupportedHostsOrInvalidCapacityAreRejectedBeforeLoadingALibrary()
    {
        if (!NativeWalPlatform.IsSupported(OperatingSystem.IsLinux(), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture))
        {
            Assert.Throws<PlatformNotSupportedException>(() => new LiburingWalContext("unused", 4096, 8));
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LiburingWalContext("unused", 0, 8));
        }
    }

    private static async Task CaptureAppendAsync(NativeWalFile file)
    {
        await file.AppendAsync(new byte[1]);
    }

    private sealed class TestSignal : INativeWalSignal
    {
        private readonly NativeWalSignal _signal = new();

        public TaskCompletionSource<Thread> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Timeout { get; private set; }
        public int Pulses { get; private set; }
        public bool Disposed { get; private set; }

        public bool Wait(int millisecondsTimeout)
        {
            Timeout = millisecondsTimeout;
            Entered.TrySetResult(Thread.CurrentThread);
            return _signal.Wait(millisecondsTimeout);
        }

        public void Pulse()
        {
            Pulses++;
            _signal.Pulse();
        }

        public void Dispose()
        {
            Disposed = true;
            _signal.Dispose();
        }
    }

    private sealed class TestContext : INativeWalContext
    {
        public IOException Failure { get; } = new("injected native failure");
        public string FailingOperation { get; init; } = string.Empty;
        public bool Gated { get; init; }
        public bool Registered { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public List<byte[]> Records { get; } = [];
        public List<int> CompletionThreads { get; } = [];
        public int AppendCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public int TruncateCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool Disposed { get; private set; }

        public bool IsFileRegistered
        {
            get
            {
                ThrowIfRequested("registration");
                return Registered;
            }
        }

        public void Append(ReadOnlyMemory<byte> record)
        {
            AppendCalls++;
            ThrowIfRequested("append");
            Records.Add(record.ToArray());
        }

        public void Flush()
        {
            FlushCalls++;
            Complete("flush");
        }

        public void Truncate()
        {
            TruncateCalls++;
            Complete("truncate");
        }

        private void Complete(string operation)
        {
            CompletionThreads.Add(Environment.CurrentManagedThreadId);
            Entered.TrySetResult();
            if (Gated)
            {
                Assert.True(Release.Wait(TimeSpan.FromSeconds(5)));
            }
            ThrowIfRequested(operation);
        }

        private void ThrowIfRequested(string operation)
        {
            if (FailingOperation == operation)
            {
                throw Failure;
            }
        }

        public void Dispose()
        {
            if (!Disposed)
            {
                Disposed = true;
                DisposeCalls++;
                Release.Dispose();
                ThrowIfRequested("dispose");
            }
        }
    }
}

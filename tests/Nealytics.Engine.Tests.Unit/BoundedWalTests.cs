using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class BoundedWalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-bounded-wal-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecordSaturationWaitsWithoutRentingAnotherPendingBufferAndCancellationFreesTheWaiter()
    {
        GateFile file = new();
        TelemetryEngineOptions options = Settings(records: 2, flushRecords: 1);
        await using WriteAheadLogger wal = new(Options.Create(options), file);
        Task first = wal.AppendAsync(Event(), CancellationToken.None);
        Task? second = null;
        Task? fourth = null;
        using CancellationTokenSource canceled = new();
        try
        {
            await file.FirstFlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = wal.AppendAsync(Event(), CancellationToken.None);
            await WaitForAsync(() => wal.PendingRecordCount == 2);
            Task third = wal.AppendAsync(Event(), canceled.Token);
            fourth = wal.AppendAsync(Event(), CancellationToken.None);
            Assert.False(second.IsCompleted);
            Assert.False(third.IsCompleted);
            Assert.False(fourth.IsCompleted);
            Assert.Equal(2, wal.PendingRecordCount);
            Assert.Equal(2 * file.Records[0].Length, wal.PendingByteCount);

            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
            Assert.Equal(2, wal.PendingRecordCount);
        }
        finally
        {
            canceled.Cancel();
            file.ReleaseFirstFlush.TrySetResult();
        }

        await Task.WhenAll(first, second!, fourth!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, file.Records.Count);
        Assert.Equal(3, wal.UncommittedRecordCount);
        Assert.Equal(0, wal.PendingRecordCount);
        Assert.Equal(0, wal.PendingByteCount);
    }

    [Fact]
    public async Task ByteSaturationAccountsForFramingAndDoesNotAdmitAnExtraRecord()
    {
        GateFile file = new();
        TelemetryEngineOptions options = Settings(records: 100, flushRecords: 100, bytes: 16_384);
        await using WriteAheadLogger wal = new(Options.Create(options), file);
        Task first = wal.AppendAsync(Event(1000), CancellationToken.None);
        using CancellationTokenSource canceled = new();
        List<Task> pending = [];
        int capacity = 0;
        try
        {
            await file.FirstFlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            int recordBytes = file.Records[0].Length;
            capacity = options.WalMaxPendingBytes / recordBytes;
            Assert.InRange(capacity, 2, 99);
            for (int index = 0; index < capacity + 1; index++)
            {
                pending.Add(wal.AppendAsync(Event(1000), canceled.Token));
            }
            await WaitForAsync(() => wal.PendingRecordCount == capacity);
            Assert.Equal(capacity * recordBytes, wal.PendingByteCount);
            Assert.All(pending, task => Assert.False(task.IsCompleted));
        }
        finally
        {
            canceled.Cancel();
            file.ReleaseFirstFlush.TrySetResult();
        }

        await first.WaitAsync(TimeSpan.FromSeconds(5));
        int canceledCount = 0;
        foreach (Task task in pending)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                canceledCount++;
            }
        }
        Assert.Equal(2, canceledCount);
        Assert.Equal(capacity, file.Records.Count);
        Assert.Equal(capacity, wal.UncommittedRecordCount);
        Assert.Equal(0, wal.PendingByteCount);
        Assert.Equal(0, wal.PendingRecordCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(100)]
    public async Task FlushGroupsRespectBothRecordAndByteBounds(int flushRecords)
    {
        GateFile file = new();
        TelemetryEngineOptions options = Settings(records: 100, flushRecords: flushRecords, bytes: 65_536);
        await using WriteAheadLogger wal = new(Options.Create(options), file);
        Task first = wal.AppendAsync(Event(1000), CancellationToken.None);
        Task[] queued = [];
        try
        {
            await file.FirstFlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queued = Enumerable.Range(0, 40).Select(_ => wal.AppendAsync(Event(1000), CancellationToken.None)).ToArray();
            await WaitForAsync(() => wal.PendingRecordCount == 41);
        }
        finally
        {
            file.ReleaseFirstFlush.TrySetResult();
        }
        await Task.WhenAll(queued.Append(first)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(file.Groups, group =>
        {
            Assert.InRange(group.Records, 1, options.WalMaxFlushRecords);
            Assert.InRange(group.Bytes, 1, options.WalMaxFlushBytes);
        });
        Assert.Equal(41, file.Groups.Sum(group => group.Records));
        Assert.Equal(41, wal.FlushedRecordCount);
        Assert.Equal(file.Groups.Count, wal.FlushGroupCount);
        Assert.Equal(41, file.Records.Select(record => JsonSerializer.Deserialize(
            record, TelemetryAotContext.Default.GlobalTelemetryPayload)!.EventId).Distinct().Count());
        Assert.Equal(0, wal.PendingByteCount);
    }

    [Fact]
    public async Task CancellationAfterAdmissionWaitsForTheActualDurabilityOutcome()
    {
        GateFile file = new();
        await using WriteAheadLogger wal = new(Options.Create(Settings()), file);
        using CancellationTokenSource canceled = new();
        Task append = wal.AppendAsync(Event(), canceled.Token);
        try
        {
            await file.FirstFlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            canceled.Cancel();
            Assert.False(append.IsCompleted);
            Assert.Equal(1, wal.PendingRecordCount);
            Assert.Equal(0, wal.UncommittedRecordCount);
        }
        finally
        {
            file.ReleaseFirstFlush.TrySetResult();
        }
        await append.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, wal.UncommittedRecordCount);
        Assert.Equal(0, wal.PendingRecordCount);
    }

    [Fact]
    public async Task ShutdownWakesAdmissionWaitersAndConcurrentDisposalOwnsTheFileOnce()
    {
        GateFile file = new();
        WriteAheadLogger wal = new(Options.Create(Settings(records: 1, flushRecords: 1)), file);
        Task first = wal.AppendAsync(Event(), CancellationToken.None);
        await file.FirstFlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task waiting = wal.AppendAsync(Event(), CancellationToken.None);
        Task serializedWaiter = wal.AppendAsync(Event(), CancellationToken.None);
        Task[] disposals = Enumerable.Range(0, 8).Select(_ => wal.DisposeAsync().AsTask()).ToArray();
        try
        {
            Assert.All(disposals, disposal => Assert.Same(disposals[0], disposal));
            Assert.False(disposals[0].IsCompleted);
            Assert.False(file.Disposed);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => serializedWaiter);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => wal.AppendAsync(Event(), CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => wal.TruncateAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => wal.AcknowledgeCommitAsync(1));
        }
        finally
        {
            file.ReleaseFirstFlush.TrySetResult();
        }

        await Task.WhenAll(disposals.Append(first)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, file.DisposeCalls);
        Assert.Single(file.Records);
        Assert.Equal(0, wal.PendingByteCount);
        Assert.Equal(0, wal.PendingRecordCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUncertainWriteOrFlushFailureFaultsTheQueueAndPreventsLaterWrites(bool failWrite)
    {
        GateFile file = new() { FailAppendAt = failWrite ? 3 : 0, FailFlushAt = failWrite ? 0 : 2 };
        await using WriteAheadLogger wal = new(Options.Create(Settings()), file);
        Task first = wal.AppendAsync(Event(), CancellationToken.None);
        Task[] queued = [];
        try
        {
            await file.FirstFlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queued = Enumerable.Range(0, 5).Select(_ => wal.AppendAsync(Event(), CancellationToken.None)).ToArray();
            await WaitForAsync(() => wal.PendingRecordCount == 6);
        }
        finally
        {
            file.ReleaseFirstFlush.TrySetResult();
        }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (Task pending in queued)
        {
            Assert.Same(file.Failure, await Assert.ThrowsAsync<IOException>(() => pending));
        }
        int writes = file.AppendCalls;
        Assert.Same(file.Failure, await Assert.ThrowsAsync<IOException>(
            () => wal.AppendAsync(Event(), CancellationToken.None)));
        Assert.Same(file.Failure, await Assert.ThrowsAsync<IOException>(() => wal.AcknowledgeCommitAsync(1)));
        Assert.Equal(writes, file.AppendCalls);
        Assert.Equal(1, wal.UncommittedRecordCount);
        Assert.Equal(1, wal.FlushedRecordCount);
        Assert.Equal(0, wal.PendingRecordCount);
        Assert.Equal(0, wal.PendingByteCount);
    }

    [Fact]
    public async Task OversizedSerializationDoesNotRentAPendingBufferOrPoisonTheWriter()
    {
        GateFile file = new(gate: false);
        await using WriteAheadLogger wal = new(Options.Create(Settings()), file);
        await Assert.ThrowsAsync<InvalidDataException>(() => wal.AppendAsync(Event(100_000), CancellationToken.None));
        Assert.Equal(0, wal.PendingRecordCount);
        Assert.Equal(0, wal.PendingByteCount);
        Assert.Empty(file.Records);

        await wal.AppendAsync(Event(), CancellationToken.None);
        Assert.Single(file.Records);
        Assert.Equal(1, wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task DisposalReportsFileFailureAndRepeatedDisposalHasTheSameOutcome()
    {
        GateFile file = new(gate: false) { FailDispose = true };
        WriteAheadLogger wal = new(Options.Create(Settings()), file);
        Task disposal = wal.DisposeAsync().AsTask();
        Assert.Same(file.Failure, await Assert.ThrowsAsync<IOException>(() => disposal));
        Assert.Same(disposal, wal.DisposeAsync().AsTask());
        Assert.Equal(1, file.DisposeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => wal.AppendAsync(Event(), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClosureBetweenReservationAndQueueingReturnsTheRentedBuffer(bool storageFailure)
    {
        GateFile file = new(gate: false) { FailTruncate = storageFailure };
        using RecordPool pool = new() { BlockRent = true };
        WriteAheadLogger wal = new(Options.Create(Settings()), file, pool);
        Task append = Task.Run(() => wal.AppendAsync(Event(), CancellationToken.None));
        Task? disposal = null;
        try
        {
            await pool.RentEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, wal.PendingRecordCount);
            if (storageFailure)
            {
                Assert.Same(file.Failure, await Assert.ThrowsAsync<IOException>(() => wal.TruncateAsync()));
            }
            else
            {
                disposal = wal.DisposeAsync().AsTask();
                Assert.False(disposal.IsCompleted);
            }
        }
        finally
        {
            pool.ReleaseRent.Set();
        }

        if (storageFailure)
        {
            Assert.Same(file.Failure, await Assert.ThrowsAsync<IOException>(() => append));
        }
        else
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => append);
        }
        await (disposal ?? wal.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, pool.RentCalls);
        Assert.Equal(1, pool.ReturnCalls);
        Assert.Equal(0, pool.Outstanding);
        Assert.True(pool.AllReturnsCleared);
        Assert.Equal(0, wal.PendingRecordCount);
        Assert.Equal(0, wal.PendingByteCount);
        Assert.Empty(file.Records);
    }

    [Fact]
    public async Task AFailedBufferRentalReturnsItsReservationWithoutPoisoningTheWriter()
    {
        GateFile file = new(gate: false);
        using RecordPool pool = new() { FailRent = true };
        await using WriteAheadLogger wal = new(Options.Create(Settings()), file, pool);
        Assert.Same(pool.Failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => wal.AppendAsync(Event(), CancellationToken.None)));
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.ReturnCalls);
        Assert.Equal(0, wal.PendingRecordCount);
        Assert.Equal(0, wal.PendingByteCount);

        pool.FailRent = false;
        await wal.AppendAsync(Event(), CancellationToken.None);
        Assert.Single(file.Records);
        Assert.Equal(2, pool.RentCalls);
        Assert.Equal(1, pool.ReturnCalls);
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulAndFailedFlushesReturnEveryOwnedBufferExactlyOnce(bool fail)
    {
        GateFile file = new(gate: false) { FailFlushAt = fail ? 1 : 0 };
        using RecordPool pool = new();
        await using WriteAheadLogger wal = new(Options.Create(Settings()), file, pool);
        Task append = wal.AppendAsync(Event(), CancellationToken.None);
        if (fail)
        {
            Assert.Same(file.Failure, await Assert.ThrowsAsync<IOException>(() => append));
        }
        else
        {
            await append;
        }
        Assert.Equal(1, pool.RentCalls);
        Assert.Equal(1, pool.ReturnCalls);
        Assert.Equal(0, pool.Outstanding);
        Assert.True(pool.AllReturnsCleared);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveManagedFileBufferSettingsKeepTheCompatibleDefault(int fileBufferBytes)
    {
        TelemetryEngineOptions options = Settings();
        options.WalFileBufferBytes = fileBufferBytes;
        await using WriteAheadLogger wal = new(Options.Create(options));
        GlobalTelemetryPayload payload = Event();
        await wal.AppendAsync(payload, CancellationToken.None);
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(_directory, "telemetry_wal.log"));
        Assert.Equal(payload.EventId, JsonSerializer.Deserialize(
            bytes, TelemetryAotContext.Default.GlobalTelemetryPayload)!.EventId);
    }

    private TelemetryEngineOptions Settings(int records = 100, int flushRecords = 2, int bytes = 65_536) => new()
    {
        WriteAheadLogDirectory = _directory,
        WalMaxPendingRecords = records,
        WalMaxPendingBytes = bytes,
        WalMaxRecordBytes = 16_384,
        WalMaxFlushRecords = flushRecords,
        WalMaxFlushBytes = 16_384
    };

    private static GlobalTelemetryPayload Event(int metadataLength = 0) => new()
    {
        ProjectId = "p",
        TenantId = "t",
        SessionId = "s",
        EventType = "e",
        Timestamp = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc),
        MetadataJson = new string('a', metadataLength)
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private sealed class GateFile : IWalFile
    {
        private int _groupCount;
        private int _groupBytes;
        private int _flushCalls;

        public GateFile(bool gate = true)
        {
            if (!gate)
            {
                ReleaseFirstFlush.SetResult();
            }
        }

        public TaskCompletionSource FirstFlushEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstFlush { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IOException Failure { get; } = new("injected uncertain storage failure");
        public List<byte[]> Records { get; } = [];
        public List<(int Records, int Bytes)> Groups { get; } = [];
        public int FailAppendAt { get; init; }
        public int FailFlushAt { get; init; }
        public bool FailDispose { get; init; }
        public bool FailTruncate { get; init; }
        public int AppendCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool Disposed { get; private set; }

        public ValueTask AppendAsync(ReadOnlyMemory<byte> record)
        {
            AppendCalls++;
            if (AppendCalls == FailAppendAt)
            {
                return ValueTask.FromException(Failure);
            }
            Records.Add(record.ToArray());
            _groupCount++;
            _groupBytes += record.Length;
            return ValueTask.CompletedTask;
        }

        public async ValueTask FlushAsync()
        {
            _flushCalls++;
            Groups.Add((_groupCount, _groupBytes));
            if (_flushCalls == 1)
            {
                FirstFlushEntered.TrySetResult();
                await ReleaseFirstFlush.Task;
            }
            if (_flushCalls == FailFlushAt)
            {
                throw Failure;
            }
            _groupCount = 0;
            _groupBytes = 0;
        }

        public ValueTask TruncateAsync() => FailTruncate ? ValueTask.FromException(Failure) : ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            DisposeCalls++;
            return FailDispose ? ValueTask.FromException(Failure) : ValueTask.CompletedTask;
        }
    }

    private sealed class RecordPool : ArrayPool<byte>, IDisposable
    {
        private readonly HashSet<byte[]> _owned = [];
        public TaskCompletionSource RentEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseRent { get; } = new();
        public InvalidOperationException Failure { get; } = new("injected rental failure");
        public bool FailRent { get; set; }
        public bool BlockRent { get; init; }
        public int RentCalls { get; private set; }
        public int ReturnCalls { get; private set; }
        public bool AllReturnsCleared { get; private set; } = true;
        public int Outstanding => _owned.Count;

        public override byte[] Rent(int minimumLength)
        {
            RentCalls++;
            RentEntered.TrySetResult();
            if (BlockRent)
            {
                Assert.True(ReleaseRent.Wait(TimeSpan.FromSeconds(5)));
            }
            if (FailRent)
            {
                throw Failure;
            }
            byte[] bytes = new byte[minimumLength];
            Assert.True(_owned.Add(bytes));
            return bytes;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(_owned.Remove(array));
            ReturnCalls++;
            AllReturnsCleared &= clearArray;
            if (clearArray)
            {
                Array.Clear(array);
            }
        }

        public void Dispose() => ReleaseRent.Dispose();
    }
}

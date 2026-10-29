using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalBatchAppendTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task InvalidStartingOffsetsAreRefusedBeforeAdmission(int startIndex)
    {
        RecordingFile file = new();
        await using Harness harness = new(file);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            harness.Wal.AppendBatchAsync([Event()], CancellationToken.None, startIndex));
        Assert.Equal(0, file.Appends);
        Assert.Equal(0, harness.Wal.PendingRecordCount);
    }

    [Fact]
    public async Task StartingOffsetsDoNotReencodeOrCommitTheAlreadyCompletedPrefix()
    {
        RecordingFile file = new();
        await using Harness harness = new(file, configure: options => options.WalMaxFlushRecords = 2);
        GlobalTelemetryPayload first = Event();
        GlobalTelemetryPayload last = Event();
        GlobalTelemetryPayload[] events = [null!, first, last];
        WalBatchAppendResult result = await harness.Wal.AppendBatchAsync(events, CancellationToken.None, 1);
        Assert.Equal(2, result.Count);
        result.ThrowIfFailed();
        Assert.Equal(Frame(first).Concat(Frame(last)), file.Bytes);
        Assert.Equal(0, (await harness.Wal.AppendBatchAsync(events, CancellationToken.None, events.Length)).Count);
        Assert.Equal(1, file.Flushes);
    }

    [Fact]
    public async Task OneContiguousGroupUsesOneBarrierAndCountsEveryLogicalRecord()
    {
        RecordingFile file = new() { HoldFlush = true };
        TrackingPool pending = new();
        TrackingPool scratch = new();
        await using Harness harness = new(file, pending: pending, scratch: scratch);
        GlobalTelemetryPayload[] events = [Event(), Event(), Event(), Event()];
        byte[] expected = events.SelectMany(Frame).ToArray();
        Task<WalBatchAppendResult> append = harness.Wal.AppendBatchAsync(events, CancellationToken.None);
        await file.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(append.IsCompleted);
        Assert.Equal(4, harness.Wal.PendingRecordCount);
        Assert.Equal(expected.Length, harness.Wal.PendingByteCount);
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        Assert.Equal(1, pending.Outstanding);
        Assert.Equal(expected, file.Bytes);
        file.Release.TrySetResult();

        WalBatchAppendResult result = await append;
        Assert.Equal(4, result.Count);
        result.ThrowIfFailed();
        Assert.Equal(1, file.Appends);
        Assert.Equal(1, file.Flushes);
        Assert.Equal(1, harness.Wal.FlushGroupCount);
        Assert.Equal(4, harness.Wal.FlushedRecordCount);
        Assert.Equal(4, harness.Wal.AverageGroupSize);
        Assert.Equal(4, harness.Wal.UncommittedRecordCount);
        Assert.Equal(0, harness.Wal.PendingRecordCount);
        Assert.Equal(0, harness.Wal.PendingByteCount);
        Assert.Equal(0, pending.Outstanding);
        await harness.Wal.AcknowledgeCommitAsync(2);
        Assert.Equal(2, harness.Wal.UncommittedRecordCount);
        Assert.Equal(0, file.Truncations);
        await harness.Wal.AcknowledgeCommitAsync(2);
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        Assert.Equal(1, file.Truncations);
        await harness.Wal.DisposeAsync();
        Assert.Equal(0, scratch.Outstanding);
        Assert.All(scratch.Returned, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGroupStopsAtItsExactRecordOrByteBudgetAndLeavesTheRemainderForTheCaller(bool byteBound)
    {
        GlobalTelemetryPayload[] events = [Event(), Event(), Event(), Event(), Event()];
        RecordingFile file = new();
        await using Harness harness = new(file, configure: options =>
        {
            options.WalMaxFlushRecords = byteBound ? 8 : 2;
            if (byteBound)
            {
                options.WalMaxFlushBytes = Frame(events[0]).Length * 2;
                options.WalMaxRecordBytes = options.WalMaxFlushBytes;
            }
        });
        int offset = 0;
        List<int> sizes = [];
        while (offset < events.Length)
        {
            WalBatchAppendResult result = await harness.Wal.AppendBatchAsync(events[offset..], CancellationToken.None);
            result.ThrowIfFailed();
            sizes.Add(result.Count);
            offset += result.Count;
        }
        Assert.Equal([2, 2, 1], sizes);
        Assert.Equal(events.SelectMany(Frame), file.Bytes);
        Assert.Equal(3, file.Flushes);
        Assert.Equal(5, harness.Wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task EncodingFailureReturnsOnlyTheDurablePrefixAndItsOriginalFailure()
    {
        RecordingFile file = new();
        await using Harness harness = new(file);
        GlobalTelemetryPayload valid = Event();
        WalBatchAppendResult result = await harness.Wal.AppendBatchAsync([valid, null!, Event()], CancellationToken.None);
        Assert.Equal(1, result.Count);
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(result.ThrowIfFailed);
        Assert.Same(result.Failure!.SourceException, error);
        Assert.Contains("EncodeBatch", error.StackTrace);
        Assert.Equal(Frame(valid), file.Bytes);
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        await harness.Wal.AppendAsync(Event(), CancellationToken.None);
        Assert.Equal(2, harness.Wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task OversizedRecordDoesNotDiscardTheEarlierCompletePrefix()
    {
        RecordingFile file = new();
        await using Harness harness = new(file);
        GlobalTelemetryPayload valid = Event();
        GlobalTelemetryPayload oversized = Event(20_000);
        WalBatchAppendResult result = await harness.Wal.AppendBatchAsync([valid, oversized], CancellationToken.None);
        Assert.Equal(1, result.Count);
        Assert.Throws<InvalidDataException>(result.ThrowIfFailed);
        Assert.Equal(Frame(valid), file.Bytes);
        Assert.Equal(1, file.Flushes);
    }

    [Fact]
    public async Task AnInvalidFirstRecordCannotCreateAnEmptyDurabilityGroup()
    {
        RecordingFile file = new();
        await using Harness harness = new(file);
        WalBatchAppendResult result = await harness.Wal.AppendBatchAsync([null!], CancellationToken.None);
        Assert.Equal(0, result.Count);
        Assert.Throws<ArgumentNullException>(result.ThrowIfFailed);
        Assert.Equal(0, file.Appends);
        Assert.Equal(0, file.Flushes);
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task EmptyGroupsPerformNoIoButStillHonorClosedAndCanceledLifetimes()
    {
        RecordingFile file = new();
        await using Harness harness = new(file);
        WalBatchAppendResult empty = await harness.Wal.AppendBatchAsync([], CancellationToken.None);
        Assert.Equal(0, empty.Count);
        Assert.Null(empty.Failure);
        empty.ThrowIfFailed();
        Assert.Equal(0, file.Flushes);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Wal.AppendBatchAsync([], canceled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Wal.AppendBatchAsync(null!, CancellationToken.None));
        await harness.Wal.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Wal.AppendBatchAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAfterQueueingCannotAcknowledgeBeforeTheSharedBarrier()
    {
        RecordingFile file = new() { HoldFlush = true };
        await using Harness harness = new(file);
        using CancellationTokenSource canceled = new();
        Task<WalBatchAppendResult> append = harness.Wal.AppendBatchAsync([Event(), Event()], canceled.Token);
        await file.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Cancel();
        Assert.False(append.IsCompleted);
        Assert.Equal(2, harness.Wal.PendingRecordCount);
        file.Release.TrySetResult();
        Assert.Equal(2, (await append).Count);
        Assert.Equal(2, harness.Wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task AWaitingGroupCancelsWithoutTakingAnyPartOfAnotherGroupsReservation()
    {
        RecordingFile file = new() { HoldFlush = true };
        await using Harness harness = new(file, configure: options => options.WalMaxPendingRecords = 4);
        GlobalTelemetryPayload[] original = [Event(), Event(), Event(), Event()];
        Task<WalBatchAppendResult> first = harness.Wal.AppendBatchAsync(original, CancellationToken.None);
        await file.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using CancellationTokenSource canceled = new();
        Task<WalBatchAppendResult> waiting = harness.Wal.AppendBatchAsync([Event(), Event()], canceled.Token);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(4, harness.Wal.PendingRecordCount);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(4, harness.Wal.PendingRecordCount);
        Assert.Equal(original.SelectMany(Frame), file.Bytes);
        file.Release.TrySetResult();
        Assert.Equal(4, (await first).Count);
        Assert.Equal(0, harness.Wal.PendingRecordCount);
        await harness.Wal.AppendAsync(Event(), CancellationToken.None);
        Assert.Equal(5, harness.Wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteOrFlushFailureCannotAcknowledgeAnyRecordAndReleasesTheEntireGroup(bool flush)
    {
        IOException failure = new("storage failed");
        RecordingFile file = new() { WriteFailure = flush ? null : failure, FlushFailure = flush ? failure : null };
        TrackingPool pending = new();
        await using Harness harness = new(file, pending: pending);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
            harness.Wal.AppendBatchAsync([Event(), Event(), Event()], CancellationToken.None)));
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        Assert.Equal(0, harness.Wal.PendingRecordCount);
        Assert.Equal(0, harness.Wal.PendingByteCount);
        Assert.Equal(0, pending.Outstanding);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
            harness.Wal.AppendBatchAsync([Event()], CancellationToken.None)));
    }

    private static GlobalTelemetryPayload Event(int size = 2_200) => new()
    {
        ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e",
        MetadataJson = JsonSerializer.Serialize(new string('x', size), TelemetryAotContext.Default.String),
        Timestamp = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc)
    };

    private static byte[] Frame(GlobalTelemetryPayload payload) =>
        [.. JsonSerializer.SerializeToUtf8Bytes(payload, TelemetryAotContext.Default.GlobalTelemetryPayload), (byte)'\n'];

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-batch-wal-" + Guid.NewGuid().ToString("N"));
        private readonly RecordingFile _file;
        public WriteAheadLogger Wal { get; }

        public Harness(RecordingFile file, Action<TelemetryEngineOptions>? configure = null,
            TrackingPool? pending = null, TrackingPool? scratch = null)
        {
            _file = file;
            TelemetryEngineOptions options = new()
            {
                WriteAheadLogDirectory = _directory,
                WalMaxPendingRecords = 8,
                WalMaxPendingBytes = 65_536,
                WalMaxRecordBytes = 8_192,
                WalMaxFlushRecords = 4,
                WalMaxFlushBytes = 16_384
            };
            configure?.Invoke(options);
            Wal = new WriteAheadLogger(Options.Create(options), file, pending, scratch);
        }

        public async ValueTask DisposeAsync()
        {
            _file.Release.TrySetResult();
            await Wal.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class RecordingFile : IWalFile
    {
        private readonly MemoryStream _bytes = new();
        public bool HoldFlush { get; init; }
        public IOException? WriteFailure { get; init; }
        public IOException? FlushFailure { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Bytes => _bytes.ToArray();
        public int Appends { get; private set; }
        public int Flushes { get; private set; }
        public int Truncations { get; private set; }

        public ValueTask AppendAsync(ReadOnlyMemory<byte> data)
        {
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }
            Appends++;
            _bytes.Write(data.Span);
            return ValueTask.CompletedTask;
        }

        public async ValueTask FlushAsync()
        {
            Flushes++;
            Entered.TrySetResult();
            if (HoldFlush)
            {
                await Release.Task;
            }
            if (FlushFailure is not null)
            {
                throw FlushFailure;
            }
        }

        public ValueTask TruncateAsync()
        {
            Truncations++;
            _bytes.SetLength(0);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            _bytes.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _owned = [];
        public int Outstanding => _owned.Count;
        public List<byte[]> Returned { get; } = [];
        public override byte[] Rent(int minimumLength)
        {
            byte[] bytes = new byte[minimumLength];
            _owned.Add(bytes);
            return bytes;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(_owned.Remove(array));
            Assert.True(clearArray);
            Array.Clear(array);
            Returned.Add(array);
        }
    }
}

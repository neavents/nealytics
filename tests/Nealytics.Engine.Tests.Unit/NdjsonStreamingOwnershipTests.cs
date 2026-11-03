using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Nealytics.Engine.Features.ImportNdjson;

namespace Nealytics.Engine.Tests.Unit;

public sealed class NdjsonStreamingOwnershipTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("")]
    public async Task ALineLargerThanTheProducerPauseThresholdCannotDeadlock(string ending)
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 32, resumeWriterThreshold: 16, useSynchronizationContext: false));
        string text = NdjsonRecordReaderTests.Event(extra: ",\"metadataJson\":\"" + new string('x', 300) + "\"") + ending;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var pool = new TrackingPool();
        Task<List<NdjsonRecord>> read = Collect(pipe.Reader, bytes.Length, pool);
        async Task Produce()
        {
            for (int index = 0; index < bytes.Length; index += 16)
                await pipe.Writer.WriteAsync(bytes.AsMemory(index, Math.Min(16, bytes.Length - index)));
            await pipe.Writer.CompleteAsync();
        }
        await Produce().WaitAsync(TimeSpan.FromSeconds(5));
        var record = Assert.Single(await read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(record.Error);
        Assert.Equal(Guid.Parse(NdjsonRecordReaderTests.Identity), record.Payload!.EventId);
        Assert.Equal(text.Length, record.WireBytes);
        Assert.Equal(bytes.Length, Assert.Single(pool.Requests));
        AssertCleared(pool);
    }

    [Fact]
    public async Task CompleteLinesDoNotRentAndEmptyReadsDoNotCreatePhantomRecords()
    {
        var pool = new TrackingPool();
        var reader = new ChunkReader([new(ReadOnlySequence<byte>.Empty, false, false), Result(NdjsonRecordReaderTests.Event() + "\n", true)]);
        var record = Assert.Single(await Collect(reader, 1024, pool));
        Assert.Null(record.Error);
        Assert.Empty(pool.Requests);
        Assert.Empty(pool.Returned);
        Assert.Equal(1, reader.Completions);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("")]
    public async Task FragmentedUtf8UsesOneOwnedBufferAcrossLinesAndExactOffsets(string ending)
    {
        string first = NdjsonRecordReaderTests.Event(extra: ",\"metadataJson\":\"日本語 é\"");
        string last = NdjsonRecordReaderTests.Event();
        byte[] firstBytes = Encoding.UTF8.GetBytes(first);
        byte[] lastBytes = Encoding.UTF8.GetBytes(last);
        var reader = new ChunkReader([
            new(new ReadOnlySequence<byte>(firstBytes.AsMemory(0, firstBytes.Length - 2)), false, false),
            new(new ReadOnlySequence<byte>(firstBytes.AsMemory(firstBytes.Length - 2)), false, false),
            Result("\r\n" + last[..10], false), Result(last[10..] + ending, true)]);
        var pool = new TrackingPool();
        var records = await Collect(reader, 1024, pool);
        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Null(record.Error));
        Assert.Equal("日本語 é", records[0].Payload!.MetadataJson);
        Assert.Equal("{}", records[1].Payload!.MetadataJson);
        Assert.Equal(firstBytes.Length + 2, records[0].WireBytes);
        Assert.Equal(lastBytes.Length + ending.Length, records[1].WireBytes);
        Assert.Equal(1024, Assert.Single(pool.Requests));
        AssertCleared(pool);
        Assert.Equal(1, reader.Completions);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("invalid")]
    [InlineData("cancelled")]
    [InlineData("reader-failed")]
    [InlineData("complete-failed")]
    public async Task EveryPartialLineExitReturnsItsClearedOwnershipAndCompletesTheReader(string scenario)
    {
        var pool = new TrackingPool();
        using var cancellation = new CancellationTokenSource();
        var reader = new ChunkReader([Result("{\"secret\":", false), Result(scenario == "oversized" ? new string('x', 1015) : "!}", true)]);
        if (scenario == "reader-failed") reader.ReadFailure = new IOException("reader failure");
        if (scenario == "complete-failed") reader.CompleteFailure = new IOException("completion failure");
        if (scenario == "cancelled") reader.OnAdvance = cancellation.Cancel;
        Task<List<NdjsonRecord>> task = Collect(reader, 1024, pool, cancellation.Token);
        if (scenario == "cancelled")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else if (scenario is "reader-failed" or "complete-failed")
            Assert.Same(reader.ReadFailure ?? reader.CompleteFailure, await Assert.ThrowsAsync<IOException>(() => task));
        else
            Assert.Equal(scenario == "oversized" ? "line_too_large" : "invalid_json", Assert.Single(await task).Error);
        AssertCleared(pool);
        Assert.Equal(1, reader.Completions);
    }

    [Fact]
    public async Task DisposingAnEnumeratorAfterOneFragmentedRecordReturnsItsBuffer()
    {
        var reader = new ChunkReader([Result(NdjsonRecordReaderTests.Event()[..10], false), Result(NdjsonRecordReaderTests.Event()[10..] + "\n" + NdjsonRecordReaderTests.Event(), true)]);
        var pool = new TrackingPool();
        await using (var iterator = NdjsonRecordReader.ReadAsync(reader, 1024, CancellationToken.None, pool).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
            Assert.Null(iterator.Current.Error);
            Assert.Empty(pool.Returned);
        }
        AssertCleared(pool);
        Assert.Equal(1, reader.Completions);
    }

    private static ReadResult Result(string text, bool completed) => new(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(text)), false, completed);

    private static async Task<List<NdjsonRecord>> Collect(PipeReader reader, int limit, TrackingPool pool, CancellationToken token = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        List<NdjsonRecord> records = [];
        await foreach (var record in NdjsonRecordReader.ReadAsync(reader, limit, deadline.Token, pool)) records.Add(record);
        return records;
    }

    private static void AssertCleared(TrackingPool pool)
    {
        var returned = Assert.Single(pool.Returned);
        Assert.True(returned.Clear);
        Assert.Same(pool.Buffer, returned.Buffer);
        Assert.All(returned.Buffer, value => Assert.Equal(0, value));
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        public byte[]? Buffer { get; private set; }
        public List<int> Requests { get; } = [];
        public List<(byte[] Buffer, bool Clear)> Returned { get; } = [];
        public override byte[] Rent(int minimumLength)
        {
            Requests.Add(minimumLength);
            return Buffer = Enumerable.Repeat((byte)17, minimumLength).ToArray();
        }
        public override void Return(byte[] array, bool clearArray = false)
        {
            Returned.Add((array, clearArray));
            if (clearArray) Array.Clear(array);
        }
    }

    private sealed class ChunkReader(ReadResult[] chunks) : PipeReader
    {
        private int _index;
        private ReadResult? _current;
        public int Completions { get; private set; }
        public Exception? ReadFailure { get; set; }
        public Exception? CompleteFailure { get; set; }
        public Action? OnAdvance { get; set; }
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (_index > 0 && ReadFailure is not null) return ValueTask.FromException<ReadResult>(ReadFailure);
            _current ??= _index < chunks.Length ? chunks[_index++] : new(ReadOnlySequence<byte>.Empty, false, true);
            return new(_current.Value);
        }
        public override void AdvanceTo(SequencePosition consumed)
        {
            var current = _current!.Value;
            var remaining = current.Buffer.Slice(consumed);
            _current = remaining.IsEmpty ? null : new ReadResult(remaining, current.IsCanceled, current.IsCompleted);
            OnAdvance?.Invoke();
        }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => AdvanceTo(consumed);
        public override void Complete(Exception? exception = null)
        {
            Completions++;
            if (CompleteFailure is not null) throw CompleteFailure;
        }
        public override void CancelPendingRead() { }
        public override bool TryRead(out ReadResult result) => throw new NotSupportedException();
    }
}

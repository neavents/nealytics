using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Nealytics.Engine.Features.ImportCsv;
using static Nealytics.Engine.Tests.Unit.NdjsonRecordReaderTests;

namespace Nealytics.Engine.Tests.Unit;

public sealed class CsvRecordReaderTests
{
    internal static async Task<List<CsvRecord>> Collect(PipeReader reader, int limit = 65_536, ArrayPool<byte>? pool = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        List<CsvRecord> records = [];
        await foreach (var record in CsvRecordReader.ReadAsync(reader, limit, deadline.Token, pool)) records.Add(record);
        return records;
    }

    public static TheoryData<string, string[]> Records => new()
    {
        { "a,b,c", ["a", "b", "c"] },
        { "a,b,c\n", ["a", "b", "c"] },
        { "a,b,c\r\n", ["a", "b", "c"] },
        { ",,", ["", "", ""] },
        { "a,", ["a", ""] },
        { "\n", [""] },
        { "\"\"", [""] },
        { "\"a,b\",\"c\"", ["a,b", "c"] },
        { "\"a\"\"b\",c", ["a\"b", "c"] },
        { "\"\"\"\"", ["\""] },
        { "\"a\r\nb\nc\rd\",é", ["a\r\nb\nc\rd", "é"] },
        { " 日本語 ,\" é \"", [" 日本語 ", " é "] },
        { "\uFEFF\"id\",value\r\n", ["id", "value"] },
        { "\uFEFE,value", ["\uFEFE", "value"] },
        { "=1+2,@command", ["=1+2", "@command"] },
        { "\"\uFFFE\",x", ["\uFFFE", "x"] }
    };

    [Theory]
    [MemberData(nameof(Records))]
    public async Task SegmentedRecordsPreserveCellsAndExactWireOffsets(string body, string[] expected)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var reader = new ProbeReader(new SegmentedReader(bytes));
        var record = Assert.Single(await Collect(reader));
        Assert.Null(record.Error);
        Assert.Equal(expected, record.Cells);
        Assert.Equal(bytes.Length, record.WireBytes);
        Assert.Equal(1, reader.Completions);
    }

    [Fact]
    public async Task EmptyBodyHasNoInventedRecordAndBomIsOnlyStrippedFromTheHeader()
    {
        Assert.Empty(await Collect(new SegmentedReader([])));
        var records = await Collect(new SegmentedReader(Encoding.UTF8.GetBytes("\uFEFFa,b\n\uFEFFc,d\n")));
        Assert.Equal(2, records.Count);
        Assert.Equal(["a", "b"], records[0].Cells!);
        Assert.Equal(["\uFEFFc", "d"], records[1].Cells!);
        Assert.Equal(Encoding.UTF8.GetBytes("\uFEFFc"), Encoding.UTF8.GetBytes(records[1].Cells![0]));
    }

    [Theory]
    [InlineData("a\"b,c")]
    [InlineData("a\"b\",c")]
    [InlineData("\"a\" b,c")]
    [InlineData("\"a\"x,c")]
    [InlineData("\"open")]
    [InlineData("\"escaped\"\"")]
    [InlineData("a\r")]
    [InlineData("a\rb\n")]
    [InlineData("\"a\"\rX")]
    public async Task InvalidGrammarStopsAtTheFirstBadRecord(string invalid)
    {
        var records = await Collect(new SegmentedReader(Encoding.UTF8.GetBytes("good,row\n" + invalid)));
        Assert.Equal(2, records.Count);
        Assert.Null(records[0].Error);
        Assert.Equal("invalid_csv", records[1].Error);
        Assert.Null(records[1].Cells!);
        Assert.Equal(0, records[1].WireBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidUtf8IsNeverReplacedInQuotedOrUnquotedCells(bool quoted)
    {
        byte[] body = quoted ? [34, 0xFF, 34, 10] : [0xEF, 0xBB];
        var result = Assert.Single(await Collect(new SegmentedReader(body)));
        Assert.Equal("invalid_utf8", result.Error);
    }

    [Theory]
    [InlineData("a\n")]
    [InlineData("\"a\nb\"\r\n")]
    [InlineData("\uFEFFa,b")]
    public async Task RecordBudgetIncludesSeparatorsQuotesBomAndEmbeddedNewlines(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var exact = Assert.Single(await Collect(new SegmentedReader(bytes), bytes.Length));
        Assert.Null(exact.Error);
        var exceeded = Assert.Single(await Collect(new SegmentedReader(bytes), bytes.Length - 1));
        Assert.Equal("record_too_large", exceeded.Error);
    }

    [Theory]
    [InlineData(256, null)]
    [InlineData(257, "column_limit")]
    public async Task ColumnsAreBoundedIndependentlyOfRecordBytes(int count, string? error)
    {
        var record = Assert.Single(await Collect(new SegmentedReader(Encoding.UTF8.GetBytes(string.Join(',', Enumerable.Repeat("x", count))))));
        Assert.Equal(error, record.Error);
        if (error is null) Assert.Equal(count, record.Cells!.Length);
    }

    [Fact]
    public async Task BackpressureCannotDeadlockARecordLargerThanThePipePauseThreshold()
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 8, resumeWriterThreshold: 4));
        string body = "\uFEFF\"a\r\n日本語\"\"b\",c\nnext,row";
        var pool = new TrackingPool();
        var collect = Collect(pipe.Reader, pool: pool);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        foreach (byte value in Encoding.UTF8.GetBytes(body)) await pipe.Writer.WriteAsync(new byte[] { value }, deadline.Token);
        await pipe.Writer.CompleteAsync();
        var records = await collect.WaitAsync(deadline.Token);
        Assert.Equal(2, records.Count);
        Assert.Equal(["a\r\n日本語\"b", "c"], records[0].Cells!);
        Assert.Equal(["next", "row"], records[1].Cells!);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), records.Sum(record => record.WireBytes));
        Assert.Equal(pool.Rents, pool.Returns);
        Assert.True(pool.Rents > 0);
        Assert.True(pool.Cleared);
    }

    [Fact]
    public async Task EarlyEnumerationExitReleasesTheReaderAndClearsThePooledRecord()
    {
        var pool = new TrackingPool();
        var reader = new ProbeReader(new SegmentedReader("a,b\nc,d"u8.ToArray()));
        await foreach (var record in CsvRecordReader.ReadAsync(reader, 1024, default, pool))
        {
            Assert.Null(record.Error);
            break;
        }
        Assert.Equal(1, reader.Completions);
        Assert.Equal(pool.Rents, pool.Returns);
        Assert.True(pool.Cleared);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidInputAndReaderCompletionFaultsStillClearTheRecordBuffer(bool completionFailure)
    {
        var pool = new TrackingPool();
        var reader = new CompletionReader(new SegmentedReader(completionFailure ? "a,b"u8.ToArray() : "a\"bad"u8.ToArray()), completionFailure);
        if (completionFailure) await Assert.ThrowsAsync<IOException>(() => Collect(reader, pool: pool));
        else Assert.Equal("invalid_csv", Assert.Single(await Collect(reader, pool: pool)).Error);
        Assert.Equal(1, reader.Completions);
        Assert.Equal(pool.Rents, pool.Returns);
        Assert.True(pool.Cleared);
    }

    [Fact]
    public async Task CallerCancellationClosesTheReaderWithoutWaitingForInput()
    {
        var reader = new ProbeReader(new SegmentedReader("a,b"u8.ToArray()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var record in CsvRecordReader.ReadAsync(reader, 1024, cancelled.Token)) { }
        });
        Assert.Equal(1, reader.Completions);
    }

    [Fact]
    public async Task ACancelledPipeReadStopsAndCompletesItsOwnership()
    {
        var pipe = new Pipe();
        pipe.Reader.CancelPendingRead();
        var reader = new ProbeReader(pipe.Reader);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(reader));
        Assert.Equal(1, reader.Completions);
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task PerRecordStateAndBudgetResetBeforeAQuotedFirstColumn()
    {
        var records = await Collect(new SegmentedReader("a\n\"b\"\n"u8.ToArray()), 4);
        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Null(record.Error));
        Assert.Equal("b", records[1].Cells![0]);
        Assert.Equal(6, records.Sum(record => record.WireBytes));
    }

    [Fact]
    public async Task MalformedQuoteAndInvalidUtf8ErrorsStopBeforeFollowingRecords()
    {
        var reader = new ProbeReader(new SegmentedReader("a\"b\",c\na,b\n"u8.ToArray()));
        Assert.Equal("invalid_csv", Assert.Single(await Collect(reader)).Error);
        Assert.Equal(2, reader.Consumed);
        var invalid = new SegmentedReader([0xFF, 44, 120, 10, 97, 44, 98, 10]);
        Assert.Equal("invalid_utf8", Assert.Single(await Collect(invalid)).Error);
        var partialBom = new SegmentedReader([0xEF, 0xBB, 34, 34]);
        Assert.Equal("invalid_csv", Assert.Single(await Collect(partialBom)).Error);
    }

    [Fact]
    public async Task CompletedInputIsNotRereadAndCancelledReadsCannotTurnIntoAnUnrelatedFailure()
    {
        var finite = new FiniteReader([
            new ReadResult(new ReadOnlySequence<byte>("a,b"u8.ToArray()), false, true),
            new ReadResult(ReadOnlySequence<byte>.Empty, false, true)]);
        Assert.Equal(["a", "b"], Assert.Single(await Collect(finite)).Cells!);
        Assert.Equal(2, finite.Reads);
        var cancelled = new FiniteReader([new ReadResult(ReadOnlySequence<byte>.Empty, true, false)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(cancelled));
        Assert.Equal(1, cancelled.Reads);
    }

    private sealed class FiniteReader(ReadResult[] results) : PipeReader
    {
        private int _index;
        private ReadResult? _current;
        public int Reads { get; private set; }
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            if (_current is null)
            {
                if (_index == results.Length) throw new IOException("Read beyond terminal input");
                _current = results[_index++];
            }
            return new(_current.Value);
        }
        public override void AdvanceTo(SequencePosition consumed)
        {
            var current = _current!.Value;
            var remaining = current.Buffer.Slice(consumed);
            _current = remaining.IsEmpty ? null : new ReadResult(remaining, current.IsCanceled, current.IsCompleted);
        }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => AdvanceTo(consumed);
        public override void Complete(Exception? exception = null) { }
        public override void CancelPendingRead() { }
        public override bool TryRead(out ReadResult result) => throw new NotSupportedException();
    }

    private sealed class CompletionReader(PipeReader inner, bool fail) : PipeReader
    {
        public int Completions { get; private set; }
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override void Complete(Exception? exception = null)
        {
            Completions++;
            inner.Complete(exception);
            if (fail) throw new IOException("completion fault");
        }
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
    }

    internal sealed class TrackingPool : ArrayPool<byte>
    {
        public int Rents { get; private set; }
        public int Returns { get; private set; }
        public bool Cleared { get; private set; } = true;
        public override byte[] Rent(int minimumLength) { Rents++; return new byte[minimumLength]; }
        public override void Return(byte[] array, bool clearArray = false) { Returns++; Cleared &= clearArray; }
    }
}

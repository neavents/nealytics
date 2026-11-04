using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Nealytics.Engine.Features.ImportNdjson;

namespace Nealytics.Engine.Tests.Unit;

public sealed class NdjsonRecordReaderTests
{
    internal const string Identity = "fa3716f8-63f6-4f59-aadc-20526905ae96";
    internal static string Event(string project = "p", string extra = "") =>
        "{\"eventId\":\"" + Identity + "\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"projectId\":\"" + project
        + "\",\"tenantId\":\"t\",\"eventType\":\"action\"" + extra + "}";

    internal static async Task<List<NdjsonRecord>> Collect(PipeReader reader, int limit = 65_536, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        List<NdjsonRecord> records = [];
        await foreach (var record in NdjsonRecordReader.ReadAsync(reader, limit, deadline.Token))
        {
            records.Add(record);
        }
        return records;
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("\n", 1)]
    [InlineData("\r\n", 1)]
    [InlineData("", 1)]
    public async Task FramingPreservesUtf8IdentifiersAndExactWireOffsets(string ending, int count)
    {
        string json = Event(extra: ",\"metadataJson\":\"日本語 é\"");
        string body = count == 0 ? "" : json + ending + (ending.Length == 0 ? "" : json);
        var reader = new ProbeReader(new SegmentedReader(Encoding.UTF8.GetBytes(body)));
        var records = await Collect(reader);
        Assert.Equal(count == 0 ? 0 : ending.Length == 0 ? 1 : 2, records.Count);
        Assert.All(records, record =>
        {
            Assert.Null(record.Error);
            Assert.Equal(Guid.Parse(Identity), record.Payload!.EventId);
            Assert.Equal("日本語 é", record.Payload.MetadataJson);
            Assert.Equal(DateTimeKind.Utc, record.Payload.Timestamp.Kind);
        });
        Assert.Equal(Encoding.UTF8.GetByteCount(body), records.Sum(record => record.WireBytes));
        Assert.Equal(1, reader.Completions);
    }

    public static TheoryData<string, string> InvalidRecords => new()
    {
        { "", "invalid_json" }, { " ", "invalid_json" }, { "\uFEFF" + Event(), "invalid_json" },
        { "{", "invalid_json" }, { "null", "invalid_record" }, { "[]", "invalid_record" },
        { "42", "invalid_record" }, { "{}", "explicit_event_id_required" },
        { Event().Replace(Identity, "invalid"), "explicit_event_id_required" },
        { Event().Replace(Identity, Guid.Empty.ToString()), "explicit_event_id_required" },
        { Event().Replace("\"" + Identity + "\"", "1"), "explicit_event_id_required" },
        { "{\"eventId\":\"" + Identity + "\"}", "explicit_utc_timestamp_required" },
        { Event().Replace("\"2026-01-01T00:00:00Z\"", "1"), "explicit_utc_timestamp_required" },
        { Event().Replace("2026-01-01T00:00:00Z", "not-a-time"), "explicit_utc_timestamp_required" },
        { Event().Replace("2026-01-01T00:00:00Z", "2026-01-01T00:00:00"), "explicit_utc_timestamp_required" },
        { Event().Replace("2026-01-01T00:00:00Z", "2026-01-01T00:00:00+00:00"), "explicit_utc_timestamp_required" },
        { Event(extra: ",\"dimensions\":{\"tag\":42}"), "invalid_json" },
        { Event().Replace("2026-01-01", "1899-12-31"), "timestamp_out_of_range" },
        { Event().Replace("2026-01-01", "2300-01-01"), "timestamp_out_of_range" },
        { Event() + "{}", "invalid_json" }
    };

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public async Task TheFirstInvalidLineStopsBeforeAnyFollowingRecord(string invalid, string error)
    {
        var reader = new ProbeReader(new SegmentedReader(Encoding.UTF8.GetBytes(Event() + "\n" + invalid + "\n" + Event())));
        var records = await Collect(reader);
        Assert.Equal(2, records.Count);
        Assert.Null(records[0].Error);
        Assert.Equal(error, records[1].Error);
        Assert.Null(records[1].Payload);
        Assert.Equal(0, records[1].WireBytes);
        Assert.Equal(1, reader.Completions);
    }

    [Theory]
    [InlineData("1900-01-01")]
    [InlineData("2299-12-31")]
    public async Task SupportedTimestampCalendarEdgesAreParsedExactly(string date)
    {
        var record = Assert.Single(await Collect(new SegmentedReader(Encoding.UTF8.GetBytes(Event().Replace("2026-01-01", date)))));
        Assert.Null(record.Error);
        Assert.Equal(date, record.Payload!.Timestamp.ToString("yyyy-MM-dd"));
    }

    [Theory]
    [InlineData(-1, "line_too_large")]
    [InlineData(0, null)]
    [InlineData(1, null)]
    public async Task LineCapsIncludeCrButExcludeLf(int delta, string? error)
    {
        string line = Event() + "\r";
        var reader = new ProbeReader(new SegmentedReader(Encoding.UTF8.GetBytes(line + "\n")));
        var record = Assert.Single(await Collect(reader, Encoding.UTF8.GetByteCount(line) + delta));
        Assert.Equal(error, record.Error);
        Assert.Equal(error is null ? Encoding.UTF8.GetByteCount(line) + 1 : 0, record.WireBytes);
        Assert.Equal(1, reader.Completions);
    }

    [Fact]
    public async Task IncompleteFragmentsTransferToBoundedOwnershipAndOversizedFragmentsStopEarly()
    {
        Pipe pipe = new();
        var reader = new ProbeReader(pipe.Reader);
        await pipe.Writer.WriteAsync("{\"eventId\":"u8.ToArray());
        Task<List<NdjsonRecord>> pending = Collect(reader);
        await reader.Advanced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(11, reader.Consumed);
        Assert.Equal(11, reader.Examined);
        Assert.False(pending.IsCompleted);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(Event()[11..]));
        await pipe.Writer.CompleteAsync();
        Assert.Null(Assert.Single(await pending.WaitAsync(TimeSpan.FromSeconds(5))).Error);
        Assert.Equal(1, reader.Completions);
        Pipe oversized = new();
        var limited = new ProbeReader(oversized.Reader);
        await oversized.Writer.WriteAsync("abc"u8.ToArray());
        Assert.Equal("line_too_large", Assert.Single(await Collect(limited, 2).WaitAsync(TimeSpan.FromSeconds(5))).Error);
        Assert.Equal(1, limited.Completions);
        await oversized.Writer.CompleteAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAlwaysCompletesTheReader(bool tokenCancellation)
    {
        Pipe pipe = new();
        var reader = new ProbeReader(pipe.Reader);
        using CancellationTokenSource cancellation = new();
        if (tokenCancellation) cancellation.Cancel();
        else
        {
            await pipe.Writer.CompleteAsync();
            pipe.Reader.CancelPendingRead();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(reader, cancellationToken: cancellation.Token));
        Assert.Equal(1, reader.Completions);
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task OversizedInputReleasesItsPipeBufferBeforeYieldingTheRejection()
    {
        var reader = new ProbeReader(new SegmentedReader("xx"u8.ToArray()));
        await using (var records = NdjsonRecordReader.ReadAsync(reader, 1, CancellationToken.None).GetAsyncEnumerator())
        {
            Assert.True(await records.MoveNextAsync());
            Assert.Equal("line_too_large", records.Current.Error);
            Assert.Equal(2, reader.Consumed);
            Assert.Equal(2, reader.Examined);
            Assert.Equal(0, reader.Completions);
        }
        Assert.Equal(1, reader.Completions);
    }

    [Fact]
    public async Task DisposingAnEnumeratorAfterOneRecordReleasesItsReader()
    {
        var reader = new ProbeReader(new SegmentedReader(Encoding.UTF8.GetBytes(Event() + "\n" + Event())));
        await using (var records = NdjsonRecordReader.ReadAsync(reader, 65_536, CancellationToken.None).GetAsyncEnumerator())
        {
            Assert.True(await records.MoveNextAsync());
        }
        Assert.Equal(1, reader.Completions);
    }

    internal sealed class ProbeReader(PipeReader inner) : PipeReader
    {
        private ReadOnlySequence<byte> _buffer;
        public int Completions { get; private set; }
        internal Action? OnAdvance { get; init; }
        public long Consumed { get; private set; }
        public long Examined { get; private set; }
        public TaskCompletionSource Advanced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            var result = await inner.ReadAsync(cancellationToken);
            _buffer = result.Buffer;
            return result;
        }
        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            Consumed = _buffer.Slice(0, consumed).Length;
            Examined = _buffer.Slice(0, examined).Length;
            inner.AdvanceTo(consumed, examined);
            OnAdvance?.Invoke();
            Advanced.TrySetResult();
        }
        public override void Complete(Exception? exception = null) { Completions++; inner.Complete(exception); }
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
    }

    internal sealed class SegmentedReader : PipeReader
    {
        private ReadOnlySequence<byte> _remaining;
        private bool _outstanding;
        private bool _completedEmpty;
        public SegmentedReader(byte[] bytes)
        {
            if (bytes.Length == 0) return;
            Segment first = new(bytes.AsMemory(0, 1));
            Segment last = first;
            foreach (byte value in bytes.AsSpan(1)) last = last.Append(new byte[] { value });
            _remaining = new(first, 0, last, 1);
        }
        private sealed class Segment : ReadOnlySequenceSegment<byte>
        {
            public Segment(ReadOnlyMemory<byte> memory) { Memory = memory; }
            public Segment Append(ReadOnlyMemory<byte> bytes)
            {
                Segment next = new(bytes) { RunningIndex = RunningIndex + Memory.Length };
                next.Memory = bytes;
                Next = next;
                return next;
            }
        }
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (_outstanding || _completedEmpty) throw new IOException("Reader progress contract was violated.");
            _outstanding = true;
            _completedEmpty = _remaining.IsEmpty;
            return new(new ReadResult(_remaining, false, true));
        }
        public override void AdvanceTo(SequencePosition consumed)
        {
            _remaining = _remaining.Slice(consumed);
            _outstanding = false;
        }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => AdvanceTo(consumed);
        public override void Complete(Exception? exception = null) { }
        public override void CancelPendingRead() { }
        public override bool TryRead(out ReadResult result) { result = new(_remaining, false, true); return true; }
    }
}

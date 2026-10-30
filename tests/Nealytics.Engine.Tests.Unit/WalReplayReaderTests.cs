using System.Buffers;
using System.Text;
using System.Text.Json;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalReplayReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(65_536)]
    public async Task PartialReadsPreserveUtf8PayloadsFramingAndOrder(int chunk)
    {
        Guid one = Guid.NewGuid();
        Guid two = Guid.NewGuid();
        byte[] first = Frame(one, "tenant-λ🙂");
        byte[] second = Frame(two, "another");
        using ChunkStream source = new([.. first, .. second], chunk);
        TrackingPool pool = new();
        List<WalReplayRecord> records = await CollectAsync(new WalReplayReader(4096, pool), source);
        Assert.Equal([one, two], records.Select(record => record.Payload.EventId));
        Assert.Equal("tenant-λ🙂", records[0].Payload.TenantId);
        Assert.Equal("another", records[1].Payload.TenantId);
        Assert.Equal(first.Length, records[0].FramedBytes);
        Assert.Equal(second.Length, records[1].FramedBytes);
        Assert.Equal(0, pool.Outstanding);
        Assert.All(pool.Returned, array => Assert.All(array, value => Assert.Equal(0, value)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    [InlineData(int.MaxValue)]
    public async Task InputReadsUseTheConfiguredBoundEvenWhenThePoolReturnsLargerArrays(int limit)
    {
        using ChunkStream source = new([], 1);
        TrackingPool pool = new();
        Assert.Empty(await CollectAsync(new WalReplayReader(limit, pool), source));
        Assert.Equal(Math.Min(65_536, limit), source.MaximumRequest);
        Assert.Equal([Math.Min(65_536, limit)], pool.Requests);
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void InvalidRecordBudgetsAreRefused(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WalReplayReader(limit));
    }

    [Fact]
    public async Task NullSourcesAreRefusedBeforeRentingBuffers()
    {
        TrackingPool pool = new();
        WalReplayReader reader = new(4096, pool);
        ArgumentNullException error = await Assert.ThrowsAsync<ArgumentNullException>(() => CollectAsync(reader, null!));
        Assert.Equal("source", error.ParamName);
        Assert.Empty(pool.Requests);
    }

    [Fact]
    public async Task ExactFramedLimitsSucceedAndOneByteSmallerLimitsFail()
    {
        byte[] frame = Frame(Guid.NewGuid());
        using MemoryStream valid = new(frame);
        Assert.Equal(frame.Length, Assert.Single(await CollectAsync(new WalReplayReader(frame.Length), valid)).FramedBytes);
        using ChunkStream oversized = new(frame, 1);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => CollectAsync(new WalReplayReader(frame.Length - 1), oversized));
        Assert.Equal("WAL frame at byte 0 exceeds WalMaxRecordBytes.", error.Message);
    }

    [Fact]
    public void FileReadsUseAsynchronousSequentialIo()
    {
        Assert.Equal(FileOptions.Asynchronous | FileOptions.SequentialScan, WalReplayReader.OpenOptions);
    }

    [Fact]
    public async Task BlankLinesCrLfAndAFirstUtf8PreambleRemainReadable()
    {
        Guid identity = Guid.NewGuid();
        byte[] framed = Frame(identity);
        byte[] crlf = [.. framed[..^1], (byte)'\r', (byte)'\n'];
        using ChunkStream source = new([.. Encoding.UTF8.GetPreamble(), (byte)'\n', .. " \t\r\n"u8.ToArray(), .. crlf], 1);
        WalReplayRecord record = Assert.Single(await CollectAsync(new WalReplayReader(4096), source));
        Assert.Equal(identity, record.Payload.EventId);
        Assert.Equal(crlf.Length, record.FramedBytes);
    }

    [Fact]
    public async Task AFirstRecordPreambleCountsTowardItsPhysicalFrameBudget()
    {
        Guid identity = Guid.NewGuid();
        byte[] frame = [.. Encoding.UTF8.GetPreamble(), .. Frame(identity)];
        using MemoryStream source = new(frame);
        WalReplayRecord record = Assert.Single(await CollectAsync(new WalReplayReader(frame.Length), source));
        Assert.Equal(identity, record.Payload.EventId);
        Assert.Equal(frame.Length, record.FramedBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"projectId\":")]
    [InlineData("not-json")]
    public async Task AnUnterminatedTailCannotBecomeAnAcknowledgedRecord(string tail)
    {
        Guid identity = Guid.NewGuid();
        using MemoryStream source = new([.. Frame(identity), .. Encoding.UTF8.GetBytes(tail)]);
        Assert.Equal(identity, Assert.Single(await CollectAsync(new WalReplayReader(4096), source)).Payload.EventId);
    }

    [Fact]
    public async Task ACompleteJsonValueWithoutItsNewlineIsStillATornFrame()
    {
        byte[] frame = Frame(Guid.NewGuid());
        using MemoryStream source = new(frame[..^1]);
        Assert.Empty(await CollectAsync(new WalReplayReader(4096), source));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"eventId\":")]
    [InlineData("{\"eventId\":42}")]
    [InlineData("{\"eventId\":null}")]
    [InlineData("{\"eventId\":\"invalid\"}")]
    public async Task CompleteMalformedFramesFailWithTheirPhysicalOffsetAndReturnAllBuffers(string corrupt)
    {
        byte[] first = Frame(Guid.NewGuid());
        using MemoryStream source = new([.. first, .. Encoding.UTF8.GetBytes(corrupt + "\n")]);
        TrackingPool pool = new();
        await using IAsyncEnumerator<WalReplayRecord> records = new WalReplayReader(4096, pool).ReadAsync(source).GetAsyncEnumerator();
        Assert.True(await records.MoveNextAsync());
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => records.MoveNextAsync().AsTask());
        Assert.Equal("Malformed WAL frame at byte " + first.Length + ".", error.Message);
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task AnExplicitZeroUuidAcceptedByIngestionRemainsStableDuringReplay()
    {
        using MemoryStream source = new(Frame(Guid.Empty));
        var recovered = Assert.Single(await CollectAsync(new WalReplayReader(4096), source));
        Assert.Equal(Guid.Empty, recovered.Payload.EventId);
    }

    public static IEnumerable<object[]> MissingFields()
    {
        string identity = "11111111-1111-4111-8111-111111111111";
        yield return ["null"];
        yield return ["{}"];
        yield return ["{\"projectId\":\"p\",\"tenantId\":\"t\",\"eventType\":\"e\"}"];
        yield return ["{\"eventId\":\"" + Guid.Empty + "\"}"];
        foreach (string missing in new[] { "projectId", "tenantId", "eventType" })
        {
            foreach (string value in new[] { "null", "\"\"" })
            {
                string json = "{\"eventId\":\"" + identity + "\",\"projectId\":\"p\",\"tenantId\":\"t\",\"eventType\":\"e\"}";
                string original = missing switch { "projectId" => "p", "tenantId" => "t", _ => "e" };
                yield return [json.Replace("\"" + missing + "\":\"" + original + "\"", "\"" + missing + "\":" + value)];
            }
        }
    }

    [Theory]
    [MemberData(nameof(MissingFields))]
    public async Task MissingScopeTypeOrStableIdentityCannotBeSilentlySkipped(string value)
    {
        using MemoryStream source = new(Encoding.UTF8.GetBytes(value + "\n"));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => CollectAsync(new WalReplayReader(4096), source));
        Assert.Equal("WAL frame at byte 0 lacks stable identity or required fields.", error.Message);
    }

    [Fact]
    public async Task IdentityCanFollowNestedUnknownFieldsButCannotBeShadowedByADuplicate()
    {
        Guid identity = Guid.NewGuid();
        string json = $$"""{"extra":{"eventId":"ignored","a":[1,2]},"projectId":"p","tenantId":"t","eventType":"e","eventId":"{{identity}}"}{{"\n"}}""";
        using MemoryStream source = new(Encoding.UTF8.GetBytes(json));
        Assert.Equal(identity, Assert.Single(await CollectAsync(new WalReplayReader(4096), source)).Payload.EventId);
        byte[] first = Frame(identity);
        string duplicate = Encoding.UTF8.GetString(first[..^2]) + ",\"eventId\":\"" + Guid.NewGuid() + "\"}\n";
        using MemoryStream ambiguous = new(Encoding.UTF8.GetBytes(duplicate));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => CollectAsync(new WalReplayReader(4096), ambiguous));
        Assert.Contains("lacks stable identity", error.Message);
    }

    [Fact]
    public async Task NestedIdentityCannotReplaceAMissingTopLevelIdentity()
    {
        string json = $$"""{"other":{"eventId":"{{Guid.NewGuid()}}"},"projectId":"p","tenantId":"t","eventType":"e"}{{"\n"}}""";
        using MemoryStream source = new(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(new WalReplayReader(4096), source));
    }

    [Fact]
    public async Task APreambleAfterTheFirstFrameIsCorruption()
    {
        using MemoryStream source = new([.. Frame(Guid.NewGuid()), .. Encoding.UTF8.GetPreamble(), .. Frame(Guid.NewGuid())]);
        await Assert.ThrowsAsync<InvalidDataException>(() => CollectAsync(new WalReplayReader(4096), source));
    }

    [Fact]
    public async Task CancellationBetweenBufferedRecordsDoesNotParseOrReadFurther()
    {
        using CancellationTokenSource cancellation = new();
        using ChunkStream source = new([.. Frame(Guid.NewGuid()), .. "broken\n"u8.ToArray()], 4096);
        TrackingPool pool = new();
        await using IAsyncEnumerator<WalReplayRecord> records = new WalReplayReader(4096, pool)
            .ReadAsync(source, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await records.MoveNextAsync());
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(() => records.MoveNextAsync().AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, source.ReadCalls);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task PreCanceledEnumerationDoesNotRentOrRead()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        TrackingPool pool = new();
        using ChunkStream source = new(Frame(Guid.NewGuid()), 4096);
        await Assert.ThrowsAsync<OperationCanceledException>(() => CollectAsync(
            new WalReplayReader(4096, pool), source, cancellation.Token));
        Assert.Empty(pool.Requests);
        Assert.Equal(0, source.ReadCalls);
    }

    [Fact]
    public async Task EarlyDisposalReturnsBuffersWithoutClosingTheBorrowedStreamOrReadingAhead()
    {
        TrackingPool pool = new();
        using ChunkStream source = new([.. Frame(Guid.NewGuid()), .. Frame(Guid.NewGuid())], 4096);
        IAsyncEnumerator<WalReplayRecord> records = new WalReplayReader(4096, pool).ReadAsync(source).GetAsyncEnumerator();
        Assert.True(await records.MoveNextAsync());
        Assert.True(pool.Outstanding > 0);
        GlobalTelemetryPayload retained = records.Current.Payload;
        await records.DisposeAsync();
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(1, source.ReadCalls);
        Assert.True(source.CanRead);
        Assert.Equal("t", retained.TenantId);
    }

    [Fact]
    public async Task ReadFailureAndBufferAllocationFailureReturnPreviouslyOwnedBuffers()
    {
        IOException failure = new("read failed");
        TrackingPool pool = new();
        using ChunkStream source = new(Frame(Guid.NewGuid()), 1) { FailAfterReads = 1, Failure = failure };
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => CollectAsync(new WalReplayReader(4096, pool), source)));
        Assert.Equal(0, pool.Outstanding);

        TrackingPool allocation = new() { FailAtRent = 2 };
        using MemoryStream readable = new(Frame(Guid.NewGuid()));
        Assert.Same(allocation.Failure, await Assert.ThrowsAsync<OutOfMemoryException>(
            () => CollectAsync(new WalReplayReader(4096, allocation), readable)));
        Assert.Equal(0, allocation.Outstanding);
        Assert.Single(allocation.Returned);
    }

    [Fact]
    public async Task LargeLogicalFilesReuseAFixedAmountOfPooledStorage()
    {
        const int limit = 4096;
        TrackingPool pool = new();
        using RepeatedStream source = new(Frame(Guid.NewGuid()), 25_000);
        long count = 0;
        await foreach (WalReplayRecord record in new WalReplayReader(limit, pool).ReadAsync(source))
        {
            Assert.Equal("t", record.Payload.TenantId);
            count++;
        }
        Assert.Equal(25_000, count);
        Assert.InRange(pool.Requests.Count, 2, 12);
        Assert.True(pool.PeakBytes <= 65_536 + 2 * limit + 51);
        Assert.Equal(0, pool.Outstanding);
    }

    private static byte[] Frame(Guid identity, string tenant = "t") => Encoding.UTF8.GetBytes(
        $$"""{"eventId":"{{identity}}","projectId":"p","tenantId":"{{tenant}}","sessionId":"s","eventType":"e"}{{"\n"}}""");

    private static async Task<List<WalReplayRecord>> CollectAsync(WalReplayReader reader, Stream source, CancellationToken cancellation = default)
    {
        List<WalReplayRecord> records = [];
        await foreach (WalReplayRecord record in reader.ReadAsync(source, cancellation))
        {
            records.Add(record);
        }
        return records;
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _owned = new(ReferenceEqualityComparer.Instance);
        private long _bytes;
        public List<int> Requests { get; } = [];
        public List<byte[]> Returned { get; } = [];
        public int Outstanding => _owned.Count;
        public long PeakBytes { get; private set; }
        public int FailAtRent { get; init; }
        public OutOfMemoryException Failure { get; } = new("rent failed");

        public override byte[] Rent(int minimumLength)
        {
            Requests.Add(minimumLength);
            if (Requests.Count == FailAtRent)
            {
                throw Failure;
            }
            byte[] array = new byte[minimumLength + 17];
            Assert.True(_owned.Add(array));
            _bytes += array.Length;
            PeakBytes = Math.Max(PeakBytes, _bytes);
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(_owned.Remove(array));
            Assert.True(clearArray);
            array.AsSpan().Clear();
            _bytes -= array.Length;
            Returned.Add(array);
        }
    }

    private sealed class ChunkStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public int MaximumRequest { get; private set; }
        public int ReadCalls { get; private set; }
        public int FailAfterReads { get; init; } = int.MaxValue;
        public IOException Failure { get; init; } = new("read failed");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumRequest = Math.Max(MaximumRequest, buffer.Length);
            if (ReadCalls++ >= FailAfterReads)
            {
                return ValueTask.FromException<int>(Failure);
            }
            return base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
        }
    }

    private sealed class RepeatedStream(byte[] frame, int repetitions) : Stream
    {
        private int _remaining = repetitions;
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int written = 0;
            while (written < buffer.Length && _remaining > 0)
            {
                int count = Math.Min(buffer.Length - written, frame.Length - _offset);
                frame.AsSpan(_offset, count).CopyTo(buffer.Span[written..]);
                written += count;
                _offset += count;
                if (_offset == frame.Length)
                {
                    _offset = 0;
                    _remaining--;
                }
            }
            return ValueTask.FromResult(written);
        }
    }
}

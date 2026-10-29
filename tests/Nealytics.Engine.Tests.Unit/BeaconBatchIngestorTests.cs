using System.Text.Json;
using System.Threading.Tasks.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Recovery accounting")]
public sealed class BeaconBatchIngestorTests
{
    private static readonly DateTime ReceivedAt = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(64)]
    public async Task ImmediatelyAvailableRecordsUseBoundedDurableGroupsAndPreserveOrder(int limit)
    {
        await using Harness harness = new(configure: options => options.BeaconWalBatchRecords = limit);
        GlobalTelemetryPayload[] events = Enumerable.Range(0, 5).Select(_ => Event()).ToArray();
        Source source = new(events);
        BeaconIngestResult result = await harness.IngestAsync(source);
        Assert.Equal(5, result.Accepted);
        Assert.Equal(0, result.Rejected);
        Assert.Equal((5 + Math.Min(limit, 4) - 1) / Math.Min(limit, 4), harness.File.Flushes);
        Assert.Equal(events.SelectMany(Frame), harness.File.Bytes);
        Assert.Equal(events.Select(item => item.EventId), harness.ReadPublished().Select(item => item.EventId));
        Assert.Equal(5, harness.Wal.UncommittedRecordCount);
        Assert.Equal(5, source.CurrentReads);
        Assert.Equal(1, source.Disposals);
    }

    [Fact]
    public async Task ByteBoundedWalPrefixesArePublishedOnceWithoutCopyingTheEarlierElementsAgain()
    {
        GlobalTelemetryPayload[] events = Enumerable.Range(0, 5).Select(_ => Event()).ToArray();
        await using Harness harness = new(configure: options =>
        {
            options.WalMaxRecordBytes = Frame(events[0]).Length * 2;
            options.WalMaxFlushBytes = options.WalMaxRecordBytes;
        });
        BeaconIngestResult result = await harness.IngestAsync(new Source(events));
        Assert.Equal(5, result.Accepted);
        Assert.Equal(3, harness.File.Flushes);
        Assert.Equal(events.SelectMany(Frame), harness.File.Bytes);
        Assert.Equal(events.Select(item => item.EventId), harness.ReadPublished().Select(item => item.EventId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task EmptyAndRejectedInputCannotSubmitEmptyWalGroups(int count)
    {
        await using Harness harness = new();
        BeaconIngestResult result = await harness.IngestAsync(new Source(new GlobalTelemetryPayload?[count]));
        Assert.Equal(0, result.Accepted);
        Assert.Equal(count, result.Rejected);
        Assert.Equal(0, harness.File.Appends);
        Assert.Equal(0, harness.File.Flushes);
        Assert.Empty(harness.ReadPublished());
    }

    [Fact]
    public async Task ValidationAndSanitizationPrecedeDurabilityForEveryAcceptedElement()
    {
        await using Harness harness = new();
        GlobalTelemetryPayload valid = new()
        {
            ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e", Timestamp = ReceivedAt,
            Dimensions = new() { ["tag"] = "kept", ["unknown"] = "removed" },
            Measures = new() { ["score"] = "7", ["unknown"] = "8" }
        };
        BeaconIngestResult result = await harness.IngestAsync(new Source([null, new(), valid]));
        Assert.Equal(1, result.Accepted);
        Assert.Equal(2, result.Rejected);
        Assert.Equal("kept", Assert.Single(valid.Dimensions).Value);
        Assert.Equal("7", Assert.Single(valid.Measures).Value);
        Assert.Equal(Frame(valid), harness.File.Bytes);
        Assert.Same(valid, Assert.Single(harness.ReadPublished()));
        IngestionKeyPolicy restricted = new(IngestionKeyScope.Public, "other", EventTypePattern.None);
        BeaconIngestResult denied = await harness.IngestAsync(new Source([Event()]), policy: restricted);
        Assert.Equal(0, denied.Accepted);
        Assert.Equal(1, denied.Rejected);
        Assert.Equal(1, harness.File.Flushes);
    }

    [Fact]
    public async Task AParsedPrefixIsDurableBeforeWaitingForTheNextBodyRead()
    {
        await using Harness harness = new(holdFlush: true);
        GlobalTelemetryPayload first = Event();
        Source source = new([first, Event()], blockAt: 1);
        Task<BeaconIngestResult> ingestion = harness.IngestAsync(source);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.File.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(source.Outstanding);
        Assert.False(ingestion.IsCompleted);
        Assert.Equal(0, harness.Broker.Reader.Count);
        harness.File.Release.TrySetResult();
        await UntilAsync(() => harness.Broker.Reader.Count == 1);
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        Assert.True(source.Outstanding);
        source.ReleaseMove();
        Assert.Equal(2, (await ingestion.WaitAsync(TimeSpan.FromSeconds(5))).Accepted);
        Assert.Equal(2, harness.File.Flushes);
        Assert.Equal(1, source.PendingResults);
        Assert.Equal(1, source.Disposals);
    }

    [Fact]
    public async Task WaitingForAnInitialBodyChunkDoesNotCreateAnEmptyBarrier()
    {
        await using Harness harness = new();
        Source source = new([Event()], blockAt: 0);
        Task<BeaconIngestResult> ingestion = harness.IngestAsync(source);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, harness.File.Flushes);
        source.ReleaseMove();
        Assert.Equal(1, (await ingestion.WaitAsync(TimeSpan.FromSeconds(5))).Accepted);
        Assert.Equal(1, harness.File.Flushes);
        Assert.Equal(1, source.PendingResults);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedOrFailedBodyReadsKeepTheirDurableValidPrefix(bool ioFailure)
    {
        await using Harness harness = new();
        Exception failure = ioFailure ? new IOException("body failed") : new JsonException("malformed tail");
        GlobalTelemetryPayload first = Event();
        Source source = new([first], failAt: 1, failure: failure);
        Exception caught = await Assert.ThrowsAnyAsync<Exception>(() => harness.IngestAsync(source));
        Assert.Same(failure, caught);
        Assert.Equal(Frame(first), harness.File.Bytes);
        Assert.Equal(first.EventId, Assert.Single(harness.ReadPublished()).EventId);
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        Assert.Equal(1, source.Disposals);
    }

    [Fact]
    public async Task FailureBeforeAnyValidElementCannotCreateADurableRecord()
    {
        await using Harness harness = new();
        JsonException failure = new("bad opening token");
        Source source = new([], failAt: 0, failure: failure);
        Assert.Same(failure, await Assert.ThrowsAsync<JsonException>(() => harness.IngestAsync(source)));
        Assert.Equal(0, harness.File.Flushes);
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageFailureCancelsAndSettlesExactlyOneOutstandingMoveBeforeDisposal(bool parserAlsoFails)
    {
        IOException failure = new("flush failed");
        await using Harness harness = new(holdFlush: true, flushFailure: failure);
        Source source = new([Event(), Event()], blockAt: 1)
        {
            CancellationFailure = parserAlsoFails ? new JsonException("late parser failure") : null
        };
        Task<BeaconIngestResult> ingestion = harness.IngestAsync(source);
        await harness.File.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.File.Release.TrySetResult();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => ingestion.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.True(source.Canceled);
        Assert.False(source.Outstanding);
        Assert.Equal(1, source.PendingResults);
        Assert.Equal(1, source.Disposals);
        Assert.Empty(harness.ReadPublished());
        Assert.Equal(0, harness.Wal.PendingRecordCount);
    }

    [Fact]
    public async Task DisconnectWhileReadingTheTailRetainsTheAlreadyDurablePrefix()
    {
        await using Harness harness = new();
        using CancellationTokenSource canceled = new();
        GlobalTelemetryPayload first = Event();
        Source source = new([first, Event()], blockAt: 1);
        Task<BeaconIngestResult> ingestion = harness.IngestAsync(source, canceled.Token);
        await UntilAsync(() => harness.Broker.Reader.Count == 1);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingestion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Frame(first), harness.File.Bytes);
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        Assert.Equal(1, source.PendingResults);
        Assert.Equal(1, source.Disposals);
    }

    [Fact]
    public async Task PublicationCancellationDoesNotReappendAnAlreadyDurableGroup()
    {
        await using Harness harness = new(configure: options => options.MemoryChannelCapacity = 1);
        using CancellationTokenSource canceled = new();
        Source source = new([Event(), Event()]);
        Task<BeaconIngestResult> ingestion = harness.IngestAsync(source, canceled.Token);
        await UntilAsync(() => harness.Broker.Reader.Count == 1 && harness.Wal.UncommittedRecordCount == 2);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingestion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, harness.Wal.UncommittedRecordCount);
        Assert.Equal(1, harness.File.Flushes);
        Assert.Single(harness.ReadPublished());
    }

    [Fact]
    public async Task EncodingFailureAfterValidInputPublishesOnlyItsDurablePrefix()
    {
        await using Harness harness = new();
        GlobalTelemetryPayload first = Event();
        Source source = new([first, Event(20_000)]);
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.IngestAsync(source));
        Assert.Equal(Frame(first), harness.File.Bytes);
        Assert.Same(first, Assert.Single(harness.ReadPublished()));
        Assert.Equal(1, harness.File.Flushes);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-1, 4)]
    [InlineData(4, 0)]
    public async Task InvalidGroupingLimitsFailBeforeInputIsRead(int requested, int flushRecords)
    {
        await using Harness harness = new();
        harness.Options.BeaconWalBatchRecords = requested;
        harness.Options.WalMaxFlushRecords = flushRecords;
        Assert.Throws<ArgumentOutOfRangeException>(harness.CreateIngestor);
    }

    [Fact]
    public async Task NullInputAndPolicyAreRejectedBeforeEnumeration()
    {
        await using Harness harness = new();
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Ingestor.IngestAsync(null!, IngestionKeyPolicy.Unpinned, ReceivedAt, CancellationToken.None));
        Source source = new([]);
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Ingestor.IngestAsync(source, null!, ReceivedAt, CancellationToken.None));
        Assert.Equal(0, source.Moves);
        Assert.Equal(0, harness.File.Flushes);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(2, deadline.Token);
        }
    }

    private static GlobalTelemetryPayload Event(int size = 2_200) => new()
    {
        ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e", Timestamp = ReceivedAt,
        MetadataJson = JsonSerializer.Serialize(new string('x', size), TelemetryAotContext.Default.String)
    };

    private static byte[] Frame(GlobalTelemetryPayload payload) =>
        [.. JsonSerializer.SerializeToUtf8Bytes(payload, TelemetryAotContext.Default.GlobalTelemetryPayload), (byte)'\n'];

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-beacon-groups-" + Guid.NewGuid().ToString("N"));
        private readonly List<Source> _sources = [];
        private readonly List<Task> _runs = [];
        public TelemetryEngineOptions Options { get; }
        public RecordingFile File { get; }
        public WriteAheadLogger Wal { get; }
        public TelemetryChannelBroker Broker { get; }
        public BeaconBatchIngestor Ingestor { get; }

        public Harness(Action<TelemetryEngineOptions>? configure = null, bool holdFlush = false, IOException? flushFailure = null)
        {
            Options = new TelemetryEngineOptions
            {
                WriteAheadLogDirectory = _directory,
                WalMaxPendingRecords = 16, WalMaxPendingBytes = 65_536, WalMaxRecordBytes = 8_192,
                WalMaxFlushRecords = 4, WalMaxFlushBytes = 16_384, MemoryChannelCapacity = 32,
                Dimensions = [new DimensionOptions { Name = "tag", Type = "String" }],
                Measures = [new MeasureOptions { Name = "score", Type = "UInt32", Aggregations = "sum" }]
            };
            configure?.Invoke(Options);
            File = new RecordingFile { HoldFlush = holdFlush, Failure = flushFailure };
            Wal = new WriteAheadLogger(Microsoft.Extensions.Options.Options.Create(Options), File);
            Broker = new TelemetryChannelBroker(Microsoft.Extensions.Options.Options.Create(Options));
            Ingestor = CreateIngestor();
        }

        public BeaconBatchIngestor CreateIngestor()
        {
            DimensionRegistry dimensions = new(Options);
            MeasureRegistry measures = new(Options, dimensions);
            return new BeaconBatchIngestor(Wal, Broker, new IngestGate(Options, measures),
                new DimensionSanitizer(dimensions, NullLogger<DimensionSanitizer>.Instance),
                new MeasureSanitizer(measures, NullLogger<MeasureSanitizer>.Instance),
                Microsoft.Extensions.Options.Options.Create(Options));
        }

        public Task<BeaconIngestResult> IngestAsync(Source source, CancellationToken cancellationToken = default, IngestionKeyPolicy? policy = null)
        {
            _sources.Add(source);
            Task<BeaconIngestResult> run = Ingestor.IngestAsync(source, policy ?? IngestionKeyPolicy.Unpinned, ReceivedAt, cancellationToken);
            _runs.Add(run);
            return run;
        }

        public List<GlobalTelemetryPayload> ReadPublished()
        {
            List<GlobalTelemetryPayload> records = [];
            while (Broker.Reader.TryRead(out GlobalTelemetryPayload? payload))
            {
                records.Add(payload);
            }
            return records;
        }

        public async ValueTask DisposeAsync()
        {
            File.Release.TrySetResult();
            foreach (Source source in _sources)
            {
                source.ReleaseMove();
            }
            ReadPublished();
            try
            {
                await Task.WhenAll(_runs).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }
            ReadPublished();
            await Wal.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class RecordingFile : IWalFile
    {
        private readonly MemoryStream _bytes = new();
        public bool HoldFlush { get; init; }
        public IOException? Failure { get; init; }
        public int Appends { get; private set; }
        public int Flushes { get; private set; }
        public byte[] Bytes => _bytes.ToArray();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask AppendAsync(ReadOnlyMemory<byte> data)
        {
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
            if (Failure is not null)
            {
                throw Failure;
            }
        }
        public ValueTask TruncateAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync()
        {
            _bytes.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Source : IAsyncEnumerable<GlobalTelemetryPayload?>, IAsyncEnumerator<GlobalTelemetryPayload?>, IValueTaskSource<bool>
    {
        private readonly GlobalTelemetryPayload?[] _items;
        private readonly int _blockAt;
        private readonly int _failAt;
        private readonly Exception? _failure;
        private ManualResetValueTaskSourceCore<bool> _pending = new() { RunContinuationsAsynchronously = true };
        private CancellationToken _token;
        private CancellationTokenRegistration _registration;
        private int _terminal;
        private int _index = -1;
        public Exception? CancellationFailure { get; init; }
        public bool Outstanding { get; private set; }
        public bool Canceled { get; private set; }
        public int Moves { get; private set; }
        public int CurrentReads { get; private set; }
        public int PendingResults { get; private set; }
        public int Disposals { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Source(GlobalTelemetryPayload?[] items, int blockAt = -1, int failAt = -1, Exception? failure = null)
        {
            _items = items;
            _blockAt = blockAt;
            _failAt = failAt;
            _failure = failure;
        }

        public GlobalTelemetryPayload? Current { get { CurrentReads++; return _items[_index]; } }
        public IAsyncEnumerator<GlobalTelemetryPayload?> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _token = cancellationToken;
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            Assert.Equal(0, Disposals);
            Assert.False(Outstanding);
            Moves++;
            _index++;
            if (_index == _failAt)
            {
                return ValueTask.FromException<bool>(_failure!);
            }
            if (_index != _blockAt)
            {
                _token.ThrowIfCancellationRequested();
                return new ValueTask<bool>(_index < _items.Length);
            }
            Outstanding = true;
            _pending.Reset();
            _registration = _token.Register(() =>
            {
                Canceled = true;
                if (Interlocked.Exchange(ref _terminal, 1) == 0)
                {
                    _pending.SetException(CancellationFailure ?? new OperationCanceledException(_token));
                }
            });
            Entered.TrySetResult();
            return new ValueTask<bool>(this, _pending.Version);
        }
        public void ReleaseMove()
        {
            if (Outstanding && Interlocked.Exchange(ref _terminal, 1) == 0)
            {
                _pending.SetResult(_index < _items.Length);
            }
        }
        public bool GetResult(short token)
        {
            Assert.True(Outstanding);
            PendingResults++;
            try
            {
                return _pending.GetResult(token);
            }
            finally
            {
                Outstanding = false;
                _registration.Dispose();
            }
        }
        public ValueTaskSourceStatus GetStatus(short token) => _pending.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _pending.OnCompleted(continuation, state, token, flags);
        public ValueTask DisposeAsync()
        {
            Assert.False(Outstanding);
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}

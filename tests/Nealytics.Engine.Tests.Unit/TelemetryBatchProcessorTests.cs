using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using NSubstitute;

namespace Nealytics.Engine.Tests.Unit;

public class TelemetryBatchProcessorTests : IAsyncDisposable
{
    private readonly string _walDir;

    public TelemetryBatchProcessorTests()
    {
        _walDir = Path.Combine(Path.GetTempPath(), $"nealytics_bp_{Guid.NewGuid():N}");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    private sealed class FakeBatchWriter : ITelemetryBatchWriter
    {
        private int _failuresRemaining;
        private readonly bool _alwaysFail;
        private readonly bool _throwCanceled;
        private readonly List<GlobalTelemetryPayload> _received = new();
        public int CallCount;

        public FakeBatchWriter(int failFirst = 0, bool alwaysFail = false, bool throwCanceled = false)
        {
            _failuresRemaining = failFirst;
            _alwaysFail = alwaysFail;
            _throwCanceled = throwCanceled;
        }

        public Task WriteAsync(IReadOnlyList<GlobalTelemetryPayload> batch, int count, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            if (_throwCanceled)
            {
                throw new OperationCanceledException("simulated cancellation");
            }
            if (_alwaysFail)
            {
                throw new InvalidOperationException("simulated permanent failure");
            }
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new InvalidOperationException("simulated transient failure");
            }
            lock (_received)
            {
                for (int i = 0; i < count; i++)
                {
                    _received.Add(batch[i]);
                }
            }
            return Task.CompletedTask;
        }

        public int TotalReceived
        {
            get { lock (_received) { return _received.Count; } }
        }
    }

    private TelemetryEngineOptions Options(int batchSize, int flushSeconds = 1, int maxRetries = 3) => new TelemetryEngineOptions
    {
        WriteAheadLogDirectory = _walDir,
        DatabaseBatchCommitSize = batchSize,
        ForceFlushIntervalSeconds = flushSeconds,
        MemoryChannelCapacity = 1000,
        MaxInsertRetries = maxRetries,
        RetryBackoffCeilingMs = 1,
        WalReplayRetryDelayMs = 1
    };

    private static IOptions<TelemetryEngineOptions> Wrap(TelemetryEngineOptions options)
    {
        IOptions<TelemetryEngineOptions> wrapped = Substitute.For<IOptions<TelemetryEngineOptions>>();
        wrapped.Value.Returns(options);
        return wrapped;
    }

    private static GlobalTelemetryPayload Event(int i) => new GlobalTelemetryPayload
    {
        ProjectId = "p",
        TenantId = "t",
        SessionId = "s",
        EventType = $"e{i}",
        MetadataJson = "{}"
    };

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(20);
        }
        return condition();
    }

    private static TelemetryBatchProcessor Build(
        TelemetryChannelBroker broker, WriteAheadLogger wal,
        ITelemetryBatchWriter writer, IOptions<TelemetryEngineOptions> options)
    {
        IHostApplicationLifetime lifetime = Substitute.For<IHostApplicationLifetime>();
        return new TelemetryBatchProcessor(
            broker, wal, writer, lifetime, options, NullLogger<TelemetryBatchProcessor>.Instance);
    }

    private async Task AppendAndPublish(WriteAheadLogger wal, TelemetryChannelBroker broker, int count)
    {
        for (int i = 0; i < count; i++)
        {
            GlobalTelemetryPayload e = Event(i);
            await wal.AppendAsync(e, CancellationToken.None);
            await broker.PublishAsync(e, CancellationToken.None);
        }
    }

    [Fact]
    public async Task SuccessfulFlush_WritesBatch_AndAcknowledgesWal()
    {
        IOptions<TelemetryEngineOptions> options = Wrap(Options(batchSize: 5));
        await using WriteAheadLogger wal = new WriteAheadLogger(options);
        TelemetryChannelBroker broker = new TelemetryChannelBroker(options);
        FakeBatchWriter writer = new FakeBatchWriter();
        TelemetryBatchProcessor processor = Build(broker, wal, writer, options);

        await AppendAndPublish(wal, broker, 5);
        await processor.StartAsync(CancellationToken.None);

        (await WaitUntil(() => writer.TotalReceived == 5)).Should().BeTrue();
        (await WaitUntil(() => wal.UncommittedRecordCount == 0)).Should().BeTrue();

        await processor.StopAsync(CancellationToken.None);
        writer.TotalReceived.Should().Be(5);
    }

    [Fact]
    public async Task TransientFailure_IsRetried_ThenCommits()
    {
        IOptions<TelemetryEngineOptions> options = Wrap(Options(batchSize: 3));
        await using WriteAheadLogger wal = new WriteAheadLogger(options);
        TelemetryChannelBroker broker = new TelemetryChannelBroker(options);
        FakeBatchWriter writer = new FakeBatchWriter(failFirst: 1);
        TelemetryBatchProcessor processor = Build(broker, wal, writer, options);

        await AppendAndPublish(wal, broker, 3);
        await processor.StartAsync(CancellationToken.None);

        (await WaitUntil(() => writer.TotalReceived == 3)).Should().BeTrue();
        await processor.StopAsync(CancellationToken.None);

        writer.CallCount.Should().BeGreaterThanOrEqualTo(2, "the first attempt failed and was retried");
        wal.UncommittedRecordCount.Should().Be(0);
    }

    [Fact]
    public async Task PermanentFailure_PreservesWal_AndDoesNotAcknowledge()
    {
        IOptions<TelemetryEngineOptions> options = Wrap(Options(batchSize: 2));
        await using WriteAheadLogger wal = new WriteAheadLogger(options);
        TelemetryChannelBroker broker = new TelemetryChannelBroker(options);
        FakeBatchWriter writer = new FakeBatchWriter(alwaysFail: true);
        TelemetryBatchProcessor processor = Build(broker, wal, writer, options);

        await AppendAndPublish(wal, broker, 2);
        await processor.StartAsync(CancellationToken.None);

        (await WaitUntil(() => writer.CallCount >= 2)).Should().BeTrue("the batch should be retried");
        await processor.StopAsync(CancellationToken.None);

        wal.UncommittedRecordCount.Should().Be(2,
            "a batch that never commits must remain in the WAL for crash recovery");
    }

    [Fact]
    public async Task WalRecovery_ReplaysSealedSegment_AndDeletesItOnSuccess()
    {
        IOptions<TelemetryEngineOptions> options = Wrap(Options(batchSize: 10));

        await using (WriteAheadLogger crashed = new WriteAheadLogger(options))
        {
            for (int i = 0; i < 4; i++)
            {
                await crashed.AppendAsync(Event(i), CancellationToken.None);
            }
        }

        await using WriteAheadLogger wal = new WriteAheadLogger(options);
        wal.HasSealedSegment.Should().BeTrue();
        TelemetryChannelBroker broker = new TelemetryChannelBroker(options);
        FakeBatchWriter writer = new FakeBatchWriter();
        TelemetryBatchProcessor processor = Build(broker, wal, writer, options);

        await processor.StartAsync(CancellationToken.None);
        (await WaitUntil(() => writer.TotalReceived == 4)).Should().BeTrue();
        await processor.StopAsync(CancellationToken.None);

        wal.HasSealedSegment.Should().BeFalse("the sealed segment is deleted after successful replay");
    }

    [Fact]
    public async Task FailedBatch_AppliesCrossBatchBackoff_AndKeepsWal()
    {
        IOptions<TelemetryEngineOptions> options = Wrap(Options(batchSize: 2, maxRetries: 1));
        await using WriteAheadLogger wal = new WriteAheadLogger(options);
        TelemetryChannelBroker broker = new TelemetryChannelBroker(options);
        FakeBatchWriter writer = new FakeBatchWriter(alwaysFail: true);
        TelemetryBatchProcessor processor = Build(broker, wal, writer, options);

        await AppendAndPublish(wal, broker, 2);
        await processor.StartAsync(CancellationToken.None);

        await Task.Delay(1400);

        await processor.StopAsync(CancellationToken.None);
        wal.UncommittedRecordCount.Should().Be(2, "a permanently failing batch stays in the WAL across backoff");
        writer.CallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task WalRecovery_RetriesReplay_WhenFirstPushFails()
    {
        IOptions<TelemetryEngineOptions> options = Wrap(Options(batchSize: 10, maxRetries: 2));

        await using (WriteAheadLogger crashed = new WriteAheadLogger(options))
        {
            for (int i = 0; i < 3; i++)
            {
                await crashed.AppendAsync(Event(i), CancellationToken.None);
            }
        }

        await using WriteAheadLogger wal = new WriteAheadLogger(options);
        TelemetryChannelBroker broker = new TelemetryChannelBroker(options);
        FakeBatchWriter writer = new FakeBatchWriter(failFirst: 2);
        TelemetryBatchProcessor processor = Build(broker, wal, writer, options);

        await processor.StartAsync(CancellationToken.None);
        (await WaitUntil(() => writer.TotalReceived == 3)).Should().BeTrue(
            "recovery retries the replay batch after the first push exhausts its retries");
        await processor.StopAsync(CancellationToken.None);

        wal.HasSealedSegment.Should().BeFalse();
    }

    [Fact]
    public async Task Shutdown_WriterThrowsCancellation_DrainDoesNotHang()
    {
        IOptions<TelemetryEngineOptions> options = Wrap(Options(batchSize: 100, flushSeconds: 60));
        await using WriteAheadLogger wal = new WriteAheadLogger(options);
        TelemetryChannelBroker broker = new TelemetryChannelBroker(options);
        FakeBatchWriter writer = new FakeBatchWriter(throwCanceled: true);
        TelemetryBatchProcessor processor = Build(broker, wal, writer, options);

        await AppendAndPublish(wal, broker, 2);
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        Func<Task> stop = () => processor.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync("a cancellation during the shutdown drain is handled, not propagated");
    }

    [Fact]
    public async Task Shutdown_DrainsRemainingChannelItems_EvenBelowBatchSize()
    {
        IOptions<TelemetryEngineOptions> options = Wrap(Options(batchSize: 100, flushSeconds: 60));
        await using WriteAheadLogger wal = new WriteAheadLogger(options);
        TelemetryChannelBroker broker = new TelemetryChannelBroker(options);
        FakeBatchWriter writer = new FakeBatchWriter();
        TelemetryBatchProcessor processor = Build(broker, wal, writer, options);

        await AppendAndPublish(wal, broker, 3);
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        await processor.StopAsync(CancellationToken.None);

        writer.TotalReceived.Should().Be(3,
            "events still in the channel at shutdown must be drained, even though the batch never filled");
    }
}

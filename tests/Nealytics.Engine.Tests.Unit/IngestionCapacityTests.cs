using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;
using NSubstitute;

namespace Nealytics.Engine.Tests.Unit;

public sealed class IngestionCapacityTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-capacity-" + Guid.NewGuid().ToString("N"));

    private TelemetryEngineOptions Settings() => new()
    {
        WriteAheadLogDirectory = _directory,
        DatabaseBatchCommitSize = 10,
        ForceFlushIntervalSeconds = 1,
        MaxInsertRetries = 1
    };

    private static GlobalTelemetryPayload Payload() => new()
    {
        ProjectId = "capacity", TenantId = "tenant", EventType = "event", SessionId = "session", Timestamp = DateTime.UtcNow.Date
    };

    [Fact]
    public void BacklogReservationsAccountForPendingAndPreviouslyWrittenBytes()
    {
        var budget = new WalBacklogBudget(100, 40);
        budget.Reserve(60);
        Assert.Equal(100, budget.Bytes);
        Assert.Throws<IOException>(() => budget.Reserve(1));
        budget.Release(40);
        Assert.Equal(60, budget.Bytes);
        budget.Reserve(40);
        budget.Release(100);
        budget.Release(0);
        Assert.Equal(0, budget.Bytes);
        Assert.Throws<InvalidOperationException>(() => budget.Release(1));
        Assert.Throws<InvalidOperationException>(() => budget.Release(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Reserve(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WalBacklogBudget(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WalBacklogBudget(1, -1));
        var inherited = new WalBacklogBudget(10, 20);
        Assert.Throws<IOException>(() => inherited.Reserve(1));
        inherited.Release(20);
        inherited.Reserve(10);
        Assert.Equal(10, inherited.Bytes);
    }

    [Fact]
    public async Task ConcurrentBacklogReservationsNeverCrossTheCapacity()
    {
        var budget = new WalBacklogBudget(100, 0);
        int accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            try
            {
                budget.Reserve(3);
                Interlocked.Increment(ref accepted);
            }
            catch (IOException)
            {
            }
        })));
        Assert.Equal(33, accepted);
        Assert.Equal(99, budget.Bytes);
    }

    [Fact]
    public async Task AFullDiskBudgetRefusesNewRecordsWithoutPoisoningTheWalAndResumesAfterCommit()
    {
        var options = Settings();
        var item = Payload();
        int bytes = JsonSerializer.SerializeToUtf8Bytes(item, TelemetryAotContext.Default.GlobalTelemetryPayload).Length + 1;
        options.WalMaxBacklogBytes = bytes * 2;
        await using var wal = new WriteAheadLogger(Options.Create(options));
        await wal.AppendAsync(item, CancellationToken.None);
        await wal.AppendAsync(Payload(), CancellationToken.None);
        Assert.Equal(bytes * 2, wal.RetainedWalBytes);
        await Assert.ThrowsAsync<IOException>(() => wal.AppendAsync(Payload(), CancellationToken.None));
        Assert.Equal(2, wal.UncommittedRecordCount);
        Assert.Equal(0, wal.PendingByteCount);
        Assert.Equal(bytes * 2, new FileInfo(Path.Combine(_directory, "telemetry_wal.log")).Length);
        await wal.AcknowledgeCommitAsync(2);
        Assert.Equal(0, wal.RetainedWalBytes);
        await wal.AppendAsync(Payload(), CancellationToken.None);
        Assert.Equal(bytes, wal.RetainedWalBytes);
        await wal.TruncateAsync();
        Assert.Equal(0, wal.RetainedWalBytes);
    }

    [Fact]
    public async Task RetainedRecoveryFilesKeepTheirDiskReservationUntilTheirDurableRemoval()
    {
        var options = Settings();
        await using (var wal = new WriteAheadLogger(Options.Create(options)))
        {
            await wal.AppendAsync(Payload(), CancellationToken.None);
        }
        options.WalMaxBacklogBytes = 1;
        await using (var wal = new WriteAheadLogger(Options.Create(options)))
        {
            Assert.True(wal.RetainedWalBytes > 1);
            await Assert.ThrowsAsync<IOException>(() => wal.AppendAsync(Payload(), CancellationToken.None));
            Assert.Single(await wal.ReplayUncommittedAsync());
            await wal.DeleteSealedSegmentAsync();
            Assert.Equal(0, wal.RetainedWalBytes);
            await wal.DeleteSealedSegmentAsync();
            Assert.Equal(0, wal.RetainedWalBytes);
        }
        options.WalMaxBacklogBytes = 1048576;
        await using var final = new WriteAheadLogger(Options.Create(options));
        await final.AppendAsync(Payload(), CancellationToken.None);
        Assert.Equal(1, final.UncommittedRecordCount);
    }

    [Fact]
    public async Task ChannelBytesBoundProducersIndependentlyOfRecordCountAndPeekingDoesNotReleaseThem()
    {
        var item = Payload();
        int bytes = TelemetryPayloadMemory.Estimate(item);
        var options = Settings();
        options.MemoryChannelCapacity = 10;
        options.MemoryChannelByteCapacity = bytes * 2;
        var broker = new TelemetryChannelBroker(Options.Create(options));
        await broker.PublishAsync(item, CancellationToken.None);
        await broker.PublishAsync(item, CancellationToken.None);
        var waiting = broker.PublishAsync(item, CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(bytes * 2, broker.RetainedBytes);
        Assert.True(broker.Reader.CanCount);
        Assert.True(broker.Reader.CanPeek);
        Assert.Equal(2, broker.Reader.Count);
        Assert.True(broker.Reader.TryPeek(out var peeked));
        Assert.Same(item, peeked);
        Assert.Equal(bytes * 2, broker.RetainedBytes);
        Assert.Same(item, await broker.Reader.ReadAsync());
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(bytes * 2, broker.RetainedBytes);
        Assert.Same(item, await broker.Reader.ReadAsync());
        Assert.Same(item, await broker.Reader.ReadAsync());
        Assert.False(broker.Reader.TryRead(out _));
        Assert.False(broker.Reader.TryPeek(out _));
        Assert.Equal(0, broker.RetainedBytes);
        Assert.False(broker.Reader.Completion.IsCompleted);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => broker.Reader.WaitToReadAsync(canceled.Token).AsTask());
    }

    [Fact]
    public async Task CanceledAndOversizedChannelWritesLeaveNoReservation()
    {
        var options = Settings();
        options.MemoryChannelByteCapacity = TelemetryPayloadMemory.Estimate(Payload());
        var broker = new TelemetryChannelBroker(Options.Create(options));
        var item = Payload();
        await broker.PublishAsync(item, CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        var waiting = broker.PublishAsync(Payload(), canceled.Token).AsTask();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(options.MemoryChannelByteCapacity, broker.RetainedBytes);
        await broker.Reader.ReadAsync();
        Assert.Equal(0, broker.RetainedBytes);
        item.MetadataJson = new string('x', 2048);
        await Assert.ThrowsAsync<IOException>(() => broker.PublishAsync(item, CancellationToken.None).AsTask());
        Assert.Equal(0, broker.RetainedBytes);
        Assert.Equal(0, broker.Reader.Count);
        await Assert.ThrowsAsync<ArgumentNullException>(() => broker.PublishAsync(null!, CancellationToken.None).AsTask());
    }

    [Fact]
    public void PayloadAccountingIncludesUnicodeAndEveryDictionaryEntry()
    {
        var empty = new GlobalTelemetryPayload();
        var item = new GlobalTelemetryPayload
        {
            ProjectId = new string('字', 1000), TenantId = "tenant", SessionId = "s", UserId = "u",
            EventType = "event", ObjectId = "o", TrafficClass = "traffic", PagePath = "path", Referrer = "ref",
            DeviceClass = "device", Os = "os", Browser = "browser", Country = "country", MetadataJson = "{}",
            Dimensions = new() { ["dimension"] = new string('字', 500) },
            Measures = new() { ["measure"] = "1" }
        };
        Assert.True(TelemetryPayloadMemory.Estimate(item) > TelemetryPayloadMemory.Estimate(empty) + 3000);
        Assert.True(TelemetryPayloadMemory.Estimate(item) > JsonSerializer.SerializeToUtf8Bytes(item,
            TelemetryAotContext.Default.GlobalTelemetryPayload).Length / 4);
        Assert.Throws<ArgumentNullException>(() => TelemetryPayloadMemory.Estimate(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseBatchesAndShutdownDrainRespectTheirByteBudget(bool replay)
    {
        var options = Settings();
        options.DatabaseBatchByteCapacity = TelemetryPayloadMemory.Estimate(Payload()) * 2;
        var items = Enumerable.Range(0, 7).Select(_ => Payload()).ToArray();
        if (replay)
        {
            await using var seed = new WriteAheadLogger(Options.Create(options));
            foreach (var item in items)
            {
                await seed.AppendAsync(item, CancellationToken.None);
            }
        }
        await using var wal = new WriteAheadLogger(Options.Create(options));
        var broker = new TelemetryChannelBroker(Options.Create(options));
        var target = new RecordingWriter();
        using var processor = new TelemetryBatchProcessor(broker, wal, target, Substitute.For<IHostApplicationLifetime>(),
            Options.Create(options), NullLogger<TelemetryBatchProcessor>.Instance);
        if (!replay)
        {
            foreach (var item in items)
            {
                await wal.AppendAsync(item, CancellationToken.None);
                await broker.PublishAsync(item, CancellationToken.None);
            }
        }
        await processor.StartAsync(CancellationToken.None);
        await (replay ? target.AllReceived.Task : target.SixReceived.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await processor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(items.Select(item => item.EventId), target.Batches.SelectMany(batch => batch).Select(item => item.EventId));
        Assert.All(target.Batches, batch => Assert.InRange(batch.Sum(TelemetryPayloadMemory.Estimate), 1, options.DatabaseBatchByteCapacity));
        Assert.Equal(new[] { 2, 2, 2, 1 }, target.Batches.Select(batch => batch.Length));
        Assert.Equal(0, broker.RetainedBytes);
        Assert.Equal(0, wal.RetainedWalBytes);
    }

    private sealed class RecordingWriter : ITelemetryBatchWriter
    {
        public readonly List<GlobalTelemetryPayload[]> Batches = [];
        public readonly TaskCompletionSource SixReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteAsync(IReadOnlyList<GlobalTelemetryPayload> batch, int count, CancellationToken cancellationToken)
        {
            Batches.Add(batch.Take(count).ToArray());
            if (Batches.Sum(items => items.Length) >= 6)
            {
                SixReceived.TrySetResult();
            }
            if (Batches.Sum(items => items.Length) == 7)
            {
                AllReceived.TrySetResult();
            }
            return Task.CompletedTask;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
        return ValueTask.CompletedTask;
    }
}

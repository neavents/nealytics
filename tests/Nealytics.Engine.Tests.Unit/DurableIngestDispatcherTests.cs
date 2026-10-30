using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using NSubstitute;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Recovery accounting")]
public sealed class DurableIngestDispatcherTests
{
    [Theory]
    [InlineData("managed", false)]
    [InlineData("managed", true)]
    [InlineData("custom", false)]
    [InlineData("custom", true)]
    public async Task CustomDispatchSurvivesCallerCancellationAfterDurability(string path, bool batch)
    {
        await using Harness harness = new(path);
        GlobalTelemetryPayload occupied = Event();
        await harness.Broker.PublishAsync(occupied, CancellationToken.None);
        GlobalTelemetryPayload[] events = batch ? [Event(), Event()] : [Event()];
        using CancellationTokenSource caller = new();
        Task dispatch = batch
            ? harness.Dispatcher.AppendBatchAsync(events, caller.Token, caller.Token)
            : harness.Dispatcher.AppendAsync(events[0], caller.Token);
        await WaitForAsync(() => harness.Wal.FlushedRecordCount == events.Length);
        caller.Cancel();
        if (path == "managed")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(occupied, await harness.Broker.Reader.ReadAsync());
            Assert.False(harness.Broker.Reader.TryRead(out _));
        }
        else
        {
            Assert.False(dispatch.IsCompleted);
            Assert.Same(occupied, await harness.Broker.Reader.ReadAsync());
            foreach (GlobalTelemetryPayload expected in events)
            {
                GlobalTelemetryPayload actual = await harness.Broker.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Same(expected, actual);
            }
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(events.Length, harness.Wal.UncommittedRecordCount);
        Assert.Equal(events.Length, (await File.ReadAllLinesAsync(harness.WalPath)).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownCanInterruptAHandoffAndLeavesItsDurableRecordsForRecovery(bool batch)
    {
        await using Harness harness = new("custom");
        await harness.Broker.PublishAsync(Event(), CancellationToken.None);
        GlobalTelemetryPayload[] events = batch ? [Event(), Event()] : [Event()];
        Task dispatch = batch
            ? harness.Dispatcher.AppendBatchAsync(events, CancellationToken.None, CancellationToken.None)
            : harness.Dispatcher.AppendAsync(events[0], CancellationToken.None);
        await WaitForAsync(() => harness.Wal.FlushedRecordCount == events.Length);
        harness.Stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(5)));
        await harness.Wal.DisposeAsync();
        await using WriteAheadLogger restarted = new(Options.Create(harness.Options));
        IReadOnlyList<GlobalTelemetryPayload> recovered = await restarted.ReplayUncommittedAsync();
        Assert.Equal(events.Select(item => item.EventId), recovered.Select(item => item.EventId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationBeforeAdmissionCannotCreateDurableOrLiveEvents(bool batch)
    {
        await using Harness harness = new("custom");
        using CancellationTokenSource caller = new();
        caller.Cancel();
        Task dispatch = batch
            ? harness.Dispatcher.AppendBatchAsync([Event()], caller.Token, caller.Token)
            : harness.Dispatcher.AppendAsync(Event(), caller.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        Assert.Empty(await File.ReadAllBytesAsync(harness.WalPath));
        Assert.False(harness.Broker.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AnEncodingFailurePublishesItsDurablePrefixOnceAndPreservesTheOriginalFailure()
    {
        await using Harness harness = new("custom", capacity: 4);
        GlobalTelemetryPayload accepted = Event();
        GlobalTelemetryPayload oversized = Event();
        oversized.MetadataJson = new string('x', 1_024);
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Dispatcher.AppendBatchAsync(
            [accepted, oversized, Event()], CancellationToken.None, CancellationToken.None));
        Assert.Same(accepted, await harness.Broker.Reader.ReadAsync());
        Assert.False(harness.Broker.Reader.TryRead(out _));
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
    }

    private static GlobalTelemetryPayload Event() => new()
    {
        ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e"
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(1, deadline.Token);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-dispatch-" + Guid.NewGuid().ToString("N"));

        public Harness(string path, int capacity = 1)
        {
            Options = new TelemetryEngineOptions
            {
                WriteAheadLogDirectory = _directory,
                IngestionExecutionPath = path,
                MemoryChannelCapacity = capacity,
                WalMaxRecordBytes = 512,
                WalMaxFlushBytes = 1_024
            };
            var options = Microsoft.Extensions.Options.Options.Create(Options);
            Wal = new WriteAheadLogger(options);
            Broker = new TelemetryChannelBroker(options);
            IHostApplicationLifetime lifetime = Substitute.For<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Returns(Stopping.Token);
            Dispatcher = new DurableIngestDispatcher(Wal, Broker, new IngestionRequestReader(options), lifetime);
        }

        public TelemetryEngineOptions Options { get; }
        public WriteAheadLogger Wal { get; }
        public TelemetryChannelBroker Broker { get; }
        public DurableIngestDispatcher Dispatcher { get; }
        public CancellationTokenSource Stopping { get; } = new();
        public string WalPath => Path.Combine(_directory, "telemetry_wal.log");

        public async ValueTask DisposeAsync()
        {
            Stopping.Cancel();
            await Wal.DisposeAsync();
            Stopping.Dispose();
            Directory.Delete(_directory, true);
        }
    }
}

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using NSubstitute;

namespace Nealytics.Engine.Tests.Unit;

[CollectionDefinition("Recovery accounting", DisableParallelization = true)]
public sealed class RecoveryAccountingCollection;

[Collection("Recovery accounting")]
public sealed partial class StreamingRecoveryTests
{
    [Theory]
    [InlineData(2, false)]
    [InlineData(10, true)]
    [InlineData(2, true)]
    public async Task RecoveryHonorsRecordAndExactByteLimitsWithoutTruncatingTheActiveWal(int countLimit, bool byteLimit)
    {
        GlobalTelemetryPayload[] events = Enumerable.Range(0, 5).Select(_ => Event()).ToArray();
        TestWriter writer = new();
        await using Harness harness = await Harness.CreateAsync(events, writer, options =>
        {
            options.DatabaseBatchCommitSize = countLimit;
            if (byteLimit)
            {
                int bytes = Frame(events[0]).Length * 2;
                options.WalMaxRecordBytes = bytes;
                options.WalMaxFlushBytes = bytes;
                options.WalMaxPendingBytes = bytes * 2;
            }
        });
        GlobalTelemetryPayload active = Event(metadata: "{}");
        await harness.Wal.AppendAsync(active, CancellationToken.None);
        byte[] activeBytes = await File.ReadAllBytesAsync(harness.ActivePath);
        await harness.Processor.StartAsync(CancellationToken.None);
        await UntilAsync(() => !harness.Wal.HasSealedSegment && harness.Logger.HasEvent(9005));
        await harness.StopAsync();

        TestWriter.Batch[] batches = writer.Attempts;
        Assert.Equal([2, 2, 1], batches.Select(batch => batch.Items.Length));
        Assert.Equal(events.Select(value => value.EventId), batches.SelectMany(batch => batch.Items).Select(value => value.EventId));
        Assert.All(batches, batch => Assert.True(batch.Items.Sum(value => Frame(value).Length) <= harness.Options.WalMaxFlushBytes));
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        Assert.Equal(activeBytes, await File.ReadAllBytesAsync(harness.ActivePath));
        Assert.Contains("recovered 5 uncommitted events", harness.Logger.Message(9005));
    }

    [Fact]
    public async Task FailedReplayRetriesTheSameBoundedPayloadsUntilCommit()
    {
        GlobalTelemetryPayload[] events = Enumerable.Range(0, 3).Select(_ => Event()).ToArray();
        TestWriter writer = new() { Failures = 2 };
        await using Harness harness = await Harness.CreateAsync(events, writer);
        await harness.Processor.StartAsync(CancellationToken.None);
        await UntilAsync(() => !harness.Wal.HasSealedSegment);
        await harness.StopAsync();

        TestWriter.Batch[] attempts = writer.Attempts;
        Assert.Equal(3, attempts.Length);
        foreach (TestWriter.Batch batch in attempts)
        {
            Assert.Equal(events.Select(value => value.EventId), batch.Items.Select(value => value.EventId));
        }
        Assert.Same(attempts[0].Items[0], attempts[1].Items[0]);
        Assert.Same(attempts[1].Items[0], attempts[2].Items[0]);
        Assert.Contains("Retrying in 1ms", harness.Logger.Message(9006));
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        Assert.Empty(await File.ReadAllBytesAsync(harness.ActivePath));
    }

    [Fact]
    public async Task ReplayDoesNotChangeTheLiveQueueGauge()
    {
        _ = TelemetryDiagnostics.VolatileQueueDepth;
        using MeterListener listener = new();
        int? depth = null;
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Name == "nealytics_queue_depth_current")
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, value, _, _) => depth = value);
        listener.Start();
        listener.RecordObservableInstruments();
        int before = depth!.Value;
        TestWriter writer = new();
        await using Harness harness = await Harness.CreateAsync([Event(), Event(), Event()], writer);
        await harness.Processor.StartAsync(CancellationToken.None);
        await UntilAsync(() => !harness.Wal.HasSealedSegment);
        await harness.StopAsync();
        listener.RecordObservableInstruments();
        Assert.Equal(before, depth);
    }

    [Fact]
    public async Task CancellationDuringAWriteKeepsTheSealedBytesAndClosesTheReader()
    {
        TestWriter writer = new() { Block = true };
        await using Harness harness = await Harness.CreateAsync([Event(), Event()], writer);
        byte[] sealedBytes = await File.ReadAllBytesAsync(harness.SealedPath);
        await harness.Processor.StartAsync(CancellationToken.None);
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.StopAsync();
        Assert.True(harness.Wal.HasSealedSegment);
        Assert.Equal(sealedBytes, await File.ReadAllBytesAsync(harness.SealedPath));
        using FileStream exclusive = new(harness.SealedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(sealedBytes.Length, exclusive.Length);
        Assert.Single(writer.Attempts);
        AssertReplayBufferReleased(harness.Processor);
    }

    [Fact]
    public async Task CancellationInterruptsReplayBackoffWithoutDroppingItsSegment()
    {
        TestWriter writer = new() { Failures = int.MaxValue };
        await using Harness harness = await Harness.CreateAsync([Event()], writer,
            options => options.WalReplayRetryDelayMs = 60_000);
        await harness.Processor.StartAsync(CancellationToken.None);
        await UntilAsync(() => harness.Logger.HasEvent(9006));
        await harness.StopAsync();
        Assert.True(harness.Wal.HasSealedSegment);
        Assert.Single(writer.Attempts);
        Assert.Contains("Retrying in 60000ms", harness.Logger.Message(9006));
        AssertReplayBufferReleased(harness.Processor);
    }

    [Fact]
    public async Task CancellationAfterTheFinalCommitStillRetainsTheSealedSegment()
    {
        using CancellationTokenSource cancellation = new();
        TestWriter writer = new() { OnSuccess = cancellation.Cancel };
        await using Harness harness = await Harness.CreateAsync([Event()], writer);
        await harness.Processor.StartAsync(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Processor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)));
        await harness.StopAsync();
        Assert.True(harness.Wal.HasSealedSegment);
        Assert.Single(writer.Attempts);
        AssertReplayBufferReleased(harness.Processor);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task CompleteCorruptionFailsRecoveryAfterItsCommittedPrefixAndPreservesTheFile(int countLimit)
    {
        GlobalTelemetryPayload first = Event();
        TestWriter writer = new();
        await using Harness harness = await Harness.CreateAsync([first], writer,
            options => options.DatabaseBatchCommitSize = countLimit, "broken\n"u8.ToArray());
        byte[] original = await File.ReadAllBytesAsync(harness.SealedPath);
        await harness.Processor.StartAsync(CancellationToken.None);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => harness.Processor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("byte " + Frame(first).Length, error.Message);
        await harness.StopAsync();
        Assert.True(harness.Wal.HasSealedSegment);
        Assert.Equal(original, await File.ReadAllBytesAsync(harness.SealedPath));
        if (countLimit == 1)
        {
            Assert.Equal(first.EventId, Assert.Single(Assert.Single(writer.Attempts).Items).EventId);
        }
        else
        {
            Assert.Empty(writer.Attempts);
        }
        Assert.False(harness.Logger.HasEvent(9005));
        AssertReplayBufferReleased(harness.Processor);
        using FileStream exclusive = new(harness.SealedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task TornTailRecoversOnlyCompleteRecordsAndDoesNotEnterTheLiveQueue()
    {
        GlobalTelemetryPayload first = Event();
        TestWriter writer = new();
        await using Harness harness = await Harness.CreateAsync([first], writer, tail: Frame(Event())[..^1]);
        await harness.Processor.StartAsync(CancellationToken.None);
        await UntilAsync(() => !harness.Wal.HasSealedSegment);
        await harness.StopAsync();
        Assert.Equal(first.EventId, Assert.Single(Assert.Single(writer.Attempts).Items).EventId);
        Assert.Equal(0, harness.Broker.Reader.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyAndBlankSegmentsDoNotCreateEmptyDatabaseBatches(bool blank)
    {
        TestWriter writer = new();
        await using Harness harness = await Harness.CreateAsync([], writer, tail: blank ? "\n \t\r\n"u8.ToArray() : []);
        await harness.Processor.StartAsync(CancellationToken.None);
        await UntilAsync(() => !harness.Wal.HasSealedSegment);
        await harness.StopAsync();
        Assert.Empty(writer.Attempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidDatabaseBatchCapacitiesFailBeforeTheProcessorStarts(int count)
    {
        TestWriter writer = new();
        await using Harness harness = await Harness.CreateAsync([], writer);
        harness.Options.DatabaseBatchCommitSize = count;
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryBatchProcessor(
            harness.Broker, harness.Wal, writer, Substitute.For<IHostApplicationLifetime>(),
            Options.Create(harness.Options), harness.Logger));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShutdownDrainHonorsTheRecordBoundForEmptyPartialAndFullBuffers(int buffered)
    {
        TestWriter writer = new();
        await using Harness harness = await Harness.CreateAsync([], writer,
            options => options.DatabaseBatchCommitSize = 2);
        GlobalTelemetryPayload[] events = Enumerable.Range(0, 5).Select(_ => Event()).ToArray();
        foreach (GlobalTelemetryPayload payload in events)
        {
            await harness.Wal.AppendAsync(payload, CancellationToken.None);
            await harness.Broker.PublishAsync(payload, CancellationToken.None);
        }
        List<GlobalTelemetryPayload> buffer = [];
        for (int index = 0; index < buffered; index++)
        {
            buffer.Add(await harness.Broker.Reader.ReadAsync());
        }
        await InvokeAsync(harness.Processor, "ExecuteCriticalDrainLoopAsync", buffer);
        Assert.Equal([2, 2, 1], writer.Attempts.Select(batch => batch.Items.Length));
        Assert.Equal(events.Select(value => value.EventId), writer.Attempts.SelectMany(batch => batch.Items).Select(value => value.EventId));
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        Assert.Empty(await File.ReadAllBytesAsync(harness.ActivePath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InterruptedShutdownDrainReportsTheActualDurableRemainder(int batchSize)
    {
        TestWriter writer = new() { Cancel = true };
        await using Harness harness = await Harness.CreateAsync([], writer,
            options => options.DatabaseBatchCommitSize = batchSize);
        List<GlobalTelemetryPayload> buffer = [Event()];
        await harness.Wal.AppendAsync(buffer[0], CancellationToken.None);
        await InvokeAsync(harness.Processor, "ExecuteCriticalDrainLoopAsync", buffer);
        Assert.Contains("1 events preserved in WAL; time budget 30s", harness.Logger.Message(9008));
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        Assert.Empty(buffer);
        Assert.NotEmpty(await File.ReadAllBytesAsync(harness.ActivePath));
    }

    [Fact]
    public async Task CrossBatchFailureStateAdvancesAfterBackoffAndResetsOnSuccess()
    {
        await using Harness harness = await Harness.CreateAsync([], new TestWriter());
        Task backoff = InvokeAsync(harness.Processor, "AwaitInsertAndBackoffAsync", Task.FromResult(false), CancellationToken.None);
        Assert.False(backoff.IsCompleted);
        await backoff;
        System.Reflection.FieldInfo failures = typeof(TelemetryBatchProcessor).GetField(
            "_consecutiveFailures", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(1, failures.GetValue(harness.Processor));
        Assert.Contains("consecutive failures: 1", harness.Logger.Message(9007));
        await InvokeAsync(harness.Processor, "AwaitInsertAndBackoffAsync", Task.FromResult(true), CancellationToken.None);
        Assert.Equal(0, failures.GetValue(harness.Processor));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private static void AssertReplayBufferReleased(TelemetryBatchProcessor processor)
    {
        System.Reflection.FieldInfo field = typeof(TelemetryBatchProcessor).GetField(
            "_bufferA", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Empty(Assert.IsType<List<GlobalTelemetryPayload>>(field.GetValue(processor)));
    }

    private static Task InvokeAsync(TelemetryBatchProcessor processor, string method, params object[] arguments)
    {
        System.Reflection.MethodInfo target = typeof(TelemetryBatchProcessor).GetMethod(
            method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return ((Task)target.Invoke(processor, arguments)!).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static GlobalTelemetryPayload Event(string? metadata = null) => new()
    {
        ProjectId = "p",
        TenantId = "t",
        SessionId = "s",
        EventType = "e",
        MetadataJson = metadata ?? JsonSerializer.Serialize(new string('x', 2_200), TelemetryAotContext.Default.String),
        Timestamp = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc)
    };

    private static byte[] Frame(GlobalTelemetryPayload payload) =>
        [.. JsonSerializer.SerializeToUtf8Bytes(payload, TelemetryAotContext.Default.GlobalTelemetryPayload), (byte)'\n'];

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory;
        public TelemetryEngineOptions Options { get; }
        public WriteAheadLogger Wal { get; }
        public TelemetryChannelBroker Broker { get; }
        public TestLogger Logger { get; } = new();
        public TelemetryBatchProcessor Processor { get; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource HostStopping { get; } = new();
        public string ActivePath => Path.Combine(_directory, "telemetry_wal.log");
        public string SealedPath => Path.Combine(_directory, "telemetry_wal.replay");

        private Harness(string directory, TelemetryEngineOptions options, TestWriter writer)
        {
            _directory = directory;
            Options = options;
            IOptions<TelemetryEngineOptions> wrapped = Microsoft.Extensions.Options.Options.Create(options);
            Wal = new WriteAheadLogger(wrapped);
            Broker = new TelemetryChannelBroker(wrapped);
            IHostApplicationLifetime lifetime = Substitute.For<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Returns(_ =>
            {
                Started.TrySetResult();
                return HostStopping.Token;
            });
            Processor = new TelemetryBatchProcessor(Broker, Wal, writer, lifetime, wrapped, Logger);
        }

        public static async Task<Harness> CreateAsync(
            GlobalTelemetryPayload[] events,
            TestWriter writer,
            Action<TelemetryEngineOptions>? configure = null,
            byte[]? tail = null)
        {
            string directory = Path.Combine(Path.GetTempPath(), "nealytics-streaming-replay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            TelemetryEngineOptions options = new()
            {
                WriteAheadLogDirectory = directory,
                DatabaseBatchCommitSize = 10,
                ForceFlushIntervalSeconds = 60,
                MaxInsertRetries = 1,
                WalReplayRetryDelayMs = 1
            };
            configure?.Invoke(options);
            await using (FileStream file = File.Create(Path.Combine(directory, "telemetry_wal.log")))
            {
                foreach (GlobalTelemetryPayload payload in events)
                {
                    await file.WriteAsync(Frame(payload));
                }
                if (tail is not null)
                {
                    await file.WriteAsync(tail);
                }
            }
            return new Harness(directory, options, writer);
        }

        public Task StopAsync() => Processor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Processor.Dispose();
            HostStopping.Dispose();
            await Wal.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class TestWriter : ITelemetryBatchWriter
    {
        public sealed record Batch(GlobalTelemetryPayload[] Items);
        private readonly List<Batch> _attempts = [];
        public int Failures { get; set; }
        public bool Block { get; init; }
        public bool Cancel { get; init; }
        public int BlockAtAttempt { get; init; }
        public bool IgnoreCancellation { get; init; }
        public TaskCompletionSource GateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnSuccess { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Batch[] Attempts { get { lock (_attempts) { return _attempts.ToArray(); } } }

        public async Task WriteAsync(IReadOnlyList<GlobalTelemetryPayload> batch, int count, CancellationToken cancellationToken)
        {
            int attempt;
            lock (_attempts)
            {
                _attempts.Add(new Batch(batch.Take(count).ToArray()));
                attempt = _attempts.Count;
            }
            Entered.TrySetResult();
            if (attempt == BlockAtAttempt)
            {
                GateEntered.TrySetResult();
                await Release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
            }
            if (Cancel)
            {
                throw new OperationCanceledException("writer canceled");
            }
            if (Block)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (Failures > 0)
            {
                Failures--;
                throw new IOException("replay write failed");
            }
            OnSuccess?.Invoke();
        }
    }

    private sealed class TestLogger : ILogger<TelemetryBatchProcessor>
    {
        private readonly List<(int Id, string Text)> _messages = [];
        public bool HasEvent(int identity) { lock (_messages) { return _messages.Any(message => message.Id == identity); } }
        public string Message(int identity) { lock (_messages) { return _messages.First(message => message.Id == identity).Text; } }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add((eventId.Id, formatter(state, exception)));
            }
        }
    }
}

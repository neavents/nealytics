using System.Diagnostics;
using System.Diagnostics.Metrics;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed partial class StreamingRecoveryTests
{
    [Fact]
    public async Task ThreeLiveBatchesAlternateBuffersWhileTheSecondWriteIsPending()
    {
        TestWriter writer = new() { BlockAtAttempt = 2, IgnoreCancellation = true };
        await using Harness harness = await Harness.CreateAsync([], writer,
            options => options.DatabaseBatchCommitSize = 2);
        GlobalTelemetryPayload[] events = Enumerable.Range(0, 6).Select(_ => Event()).ToArray();
        foreach (GlobalTelemetryPayload payload in events)
        {
            await harness.Wal.AppendAsync(payload, CancellationToken.None);
            await harness.Broker.PublishAsync(payload, CancellationToken.None);
        }
        await harness.Processor.StartAsync(CancellationToken.None);
        try
        {
            await writer.GateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await UntilAsync(() => harness.Broker.Reader.Count == 0);
            Assert.Equal(2, writer.Attempts.Length);
        }
        finally
        {
            writer.Release.TrySetResult();
        }
        await UntilAsync(() => harness.Wal.UncommittedRecordCount == 0);
        await harness.StopAsync();
        Assert.Equal([2, 2, 2], writer.Attempts.Select(batch => batch.Items.Length));
        Assert.Equal(events.Select(value => value.EventId), writer.Attempts.SelectMany(batch => batch.Items).Select(value => value.EventId));
    }

    [Fact]
    public async Task ShutdownWaitsForAStartedWriteThatOutlivesCallerCancellation()
    {
        TestWriter writer = new() { BlockAtAttempt = 1, IgnoreCancellation = true };
        await using Harness harness = await Harness.CreateAsync([], writer,
            options => options.DatabaseBatchCommitSize = 1);
        GlobalTelemetryPayload payload = Event();
        await harness.Wal.AppendAsync(payload, CancellationToken.None);
        await harness.Broker.PublishAsync(payload, CancellationToken.None);
        await harness.Processor.StartAsync(CancellationToken.None);
        await writer.GateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task stop = harness.StopAsync();
        try
        {
            Task observation = Task.Delay(200);
            Assert.Same(observation, await Task.WhenAny(stop, observation));
            Assert.False(harness.Processor.ExecuteTask!.IsCompleted);
            Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        }
        finally
        {
            writer.Release.TrySetResult();
        }
        await stop;
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        Assert.Single(writer.Attempts);
    }

    [Fact]
    public async Task AnIdleTimerKeepsTheConsumerAliveWithoutSubmittingEmptyBatches()
    {
        TestWriter writer = new();
        await using Harness harness = await Harness.CreateAsync([], writer,
            options => options.ForceFlushIntervalSeconds = 1);
        await harness.Processor.StartAsync(CancellationToken.None);
        await harness.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1200);
        Assert.False(harness.Processor.ExecuteTask!.IsCompleted);
        Assert.Empty(writer.Attempts);
        await harness.StopAsync();
    }

    [Fact]
    public async Task UnexpectedWriterCancellationLeavesTheConsumeLoopAndPreservesAllDurableRows()
    {
        TestWriter writer = new() { Cancel = true };
        await using Harness harness = await Harness.CreateAsync([], writer,
            options => options.DatabaseBatchCommitSize = 1);
        for (int index = 0; index < 2; index++)
        {
            GlobalTelemetryPayload payload = Event();
            await harness.Wal.AppendAsync(payload, CancellationToken.None);
            await harness.Broker.PublishAsync(payload, CancellationToken.None);
        }
        await harness.Processor.StartAsync(CancellationToken.None);
        await harness.Processor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, harness.Wal.UncommittedRecordCount);
        Assert.Equal(2, writer.Attempts.Length);
        Assert.True(harness.Logger.HasEvent(9008));
    }

    [Fact]
    public async Task HostTerminationRegistersItsDiagnosticBeforeTheConsumerWaits()
    {
        await using Harness harness = await Harness.CreateAsync([], new TestWriter());
        await harness.Processor.StartAsync(CancellationToken.None);
        await harness.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.HostStopping.Cancel();
        Assert.Contains("Host termination detected", harness.Logger.Message(9001));
        await harness.StopAsync();
    }

    [Fact]
    public async Task RetrySuccessEmitsTheFlushTraceCommitDurationAndBalancedLiveGauge()
    {
        using Observations observations = new();
        observations.Listener.RecordObservableInstruments();
        int before = observations.QueueDepth;
        TestWriter writer = new() { Failures = 1 };
        await using Harness harness = await Harness.CreateAsync([], writer, options =>
        {
            options.MaxInsertRetries = 2;
            options.RetryBackoffCeilingMs = 1000;
        });
        GlobalTelemetryPayload payload = Event();
        await harness.Wal.AppendAsync(payload, CancellationToken.None);
        await harness.Broker.PublishAsync(payload, CancellationToken.None);
        GlobalTelemetryPayload queued = await harness.Broker.Reader.ReadAsync();
        Task insert = InvokeAsync(harness.Processor, "PushBatchToClickHouseAsync",
            new List<GlobalTelemetryPayload> { queued }, CancellationToken.None, false);
        Assert.False(insert.IsCompleted);
        Assert.Contains("attempt 1/2 failed. Retrying in 1000ms", harness.Logger.Message(9004));
        await insert;
        observations.Listener.RecordObservableInstruments();
        Assert.Equal(before, observations.QueueDepth);
        Assert.Equal(1, observations.Commits);
        Assert.Equal("BatchProcessor.Flush", Assert.Single(observations.Activities));
        Assert.InRange(Assert.Single(observations.Durations), 0.8, 10);
        Assert.Contains("Flushing batch of 1 events", harness.Logger.Message(9002));
        Assert.False(harness.Logger.HasEvent(9003));
        Assert.Equal(2, writer.Attempts.Length);
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task ExhaustedRetriesRetainTheirErrorDiagnosticAndDoNotReportACommit()
    {
        using Observations observations = new();
        TestWriter writer = new() { Failures = int.MaxValue };
        await using Harness harness = await Harness.CreateAsync([], writer);
        GlobalTelemetryPayload payload = Event();
        await harness.Wal.AppendAsync(payload, CancellationToken.None);
        List<GlobalTelemetryPayload> buffer = [payload];
        await InvokeAsync(harness.Processor, "PushBatchToClickHouseAsync", buffer, CancellationToken.None, false);
        Assert.Contains("Events preserved in WAL", harness.Logger.Message(9003));
        Assert.False(harness.Logger.HasEvent(9004));
        Assert.Equal(0, observations.Commits);
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        Assert.InRange(Assert.Single(observations.Durations), 0, 10);
        Assert.Empty(buffer);
    }

    private sealed class Observations : IDisposable
    {
        private readonly ActivityListener _activities;
        public MeterListener Listener { get; } = new();
        public List<string> Activities { get; } = [];
        public List<double> Durations { get; } = [];
        public long Commits { get; private set; }
        public int QueueDepth { get; private set; }

        public Observations()
        {
            _ = TelemetryDiagnostics.VolatileQueueDepth;
            Listener.InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == TelemetryDiagnostics.EngineMeter.Name)
                {
                    current.EnableMeasurementEvents(instrument);
                }
            };
            Listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (instrument.Name == "nealytics_batches_committed_total")
                {
                    Commits += value;
                }
            });
            Listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            {
                if (instrument.Name == "nealytics_storage_write_duration_seconds")
                {
                    Durations.Add(value);
                }
            });
            Listener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
            {
                if (instrument.Name == "nealytics_queue_depth_current")
                {
                    QueueDepth = value;
                }
            });
            Listener.Start();
            _activities = new ActivityListener
            {
                ShouldListenTo = source => source == TelemetryDiagnostics.Source,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity => Activities.Add(activity.OperationName)
            };
            ActivitySource.AddActivityListener(_activities);
        }

        public void Dispose()
        {
            _activities.Dispose();
            Listener.Dispose();
        }
    }
}

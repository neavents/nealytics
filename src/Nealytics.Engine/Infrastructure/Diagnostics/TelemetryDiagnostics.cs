namespace Nealytics.Engine.Infrastructure.Diagnostics;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

public static class TelemetryDiagnostics
{
    public static readonly ActivitySource Source = new("Nealytics.Engine.Core");
    public static readonly Meter EngineMeter = new("Nealytics.Engine.Metrics");

    public static readonly Counter<long> IngestedEventsTotal =
        EngineMeter.CreateCounter<long>("nealytics_events_ingested_total");

    public static readonly Counter<long> StorageBatchesCommitted =
        EngineMeter.CreateCounter<long>("nealytics_batches_committed_total");

    public static readonly Counter<long> ReadQueriesExecuted =
        EngineMeter.CreateCounter<long>("nealytics_read_queries_total");

    public static readonly Histogram<double> StorageWriteDuration =
        EngineMeter.CreateHistogram<double>("nealytics_storage_write_duration_seconds");

    public static readonly Histogram<double> QueryReadDuration =
        EngineMeter.CreateHistogram<double>("nealytics_query_read_duration_seconds");

    private static int _inMemoryQueueDepth = 0;

    public static readonly ObservableGauge<int> VolatileQueueDepth =
        EngineMeter.CreateObservableGauge("nealytics_queue_depth_current", () => _inMemoryQueueDepth);

    public static void IncrementQueueCounter() => Interlocked.Increment(ref _inMemoryQueueDepth);

    public static void DecrementQueueCounter(int amount) => Interlocked.Add(ref _inMemoryQueueDepth, -amount);
}

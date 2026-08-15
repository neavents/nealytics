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

    /// <summary>
    /// Fields dropped at ingest because their key is not a declared dimension, tagged with the
    /// key and the project that sent it.
    ///
    /// An unregistered dimension vanishing without a trace is precisely the failure the declared
    /// dimension design exists to end: in this same estate a Cloudflare Worker returned 204 for
    /// three months while discarding every analytics beacon, and the only evidence was a table
    /// that stopped growing.
    /// </summary>
    public static readonly Counter<long> UnknownDimensionsDropped =
        EngineMeter.CreateCounter<long>("nealytics_unknown_dimensions_dropped_total");

    /// <summary>
    /// Values for a declared dimension that did not parse as its declared type, tagged with the
    /// dimension. The cell is written NULL; the batch still commits.
    /// </summary>
    public static readonly Counter<long> DimensionValuesRejected =
        EngineMeter.CreateCounter<long>("nealytics_dimension_values_rejected_total");

    public static readonly Counter<long> UnknownMeasuresDropped =
        EngineMeter.CreateCounter<long>("nealytics_unknown_measures_dropped_total");

    public static readonly Counter<long> MeasureValuesRejected =
        EngineMeter.CreateCounter<long>("nealytics_measure_values_rejected_total");

    /// <summary>
    /// Whole events refused at ingest, tagged with the reason and the transport.
    ///
    /// The beacon endpoint used to step past an invalid element with a bare <c>continue</c>: no
    /// counter, no log, and a 204 for the batch. A client shipping a malformed field would lose
    /// every event carrying it while every signal available said the pipeline was healthy, which
    /// is the same shape as the outage the dimension counters above exist to prevent.
    ///
    /// The reason tag comes from a closed enum rather than from anything the caller sent, so this
    /// cannot become an unbounded cardinality dimension in the metrics backend.
    /// </summary>
    public static readonly Counter<long> EventsRejected =
        EngineMeter.CreateCounter<long>("nealytics_events_rejected_total");

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

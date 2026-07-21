using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using NSubstitute;
using Xunit.Abstractions;

namespace Nealytics.Engine.Tests.Unit;

// Layered micro-benchmarks that isolate each stage of the ingest path so we can see WHERE
// the time goes instead of only measuring the end-to-end HTTP number. These run in the
// normal suite at a small size (fast + a real zero-loss assertion); set NEALYTICS_BENCH=1
// (and optionally NEALYTICS_BENCH_APPENDS) to crank them up and print full tables.
//
// Run with output visible:
//   NEALYTICS_BENCH=1 dotnet test --filter FullyQualifiedName~LayeredBenchmarks -l "console;verbosity=detailed"
public class LayeredBenchmarks
{
    private readonly ITestOutputHelper _output;

    public LayeredBenchmarks(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool BenchMode => Environment.GetEnvironmentVariable("NEALYTICS_BENCH") == "1";

    private static int Appends =>
        int.TryParse(Environment.GetEnvironmentVariable("NEALYTICS_BENCH_APPENDS"), out int n) ? n
        : BenchMode ? 100_000 : 400;

    private static int[] ConcurrencyLevels =>
        BenchMode ? new[] { 1, 8, 64, 256, 1024 } : new[] { 1, 32 };

    private static IOptions<TelemetryEngineOptions> Options(string walDir)
    {
        TelemetryEngineOptions opts = new TelemetryEngineOptions
        {
            WriteAheadLogDirectory = walDir,
            MemoryChannelCapacity = 1_000_000
        };
        IOptions<TelemetryEngineOptions> wrapped = Substitute.For<IOptions<TelemetryEngineOptions>>();
        wrapped.Value.Returns(opts);
        return wrapped;
    }

    private static GlobalTelemetryPayload SampleEvent() => new GlobalTelemetryPayload
    {
        ProjectId = "bench",
        TenantId = "bench",
        SessionId = "s",
        EventType = "bench",
        MetadataJson = "{}"
    };

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    // ─── Layer 0: raw fsync cost — the physical floor for a group of one ───

    [Fact]
    public async Task Layer0_RawFsyncCost()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nealytics_fsync_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "probe.bin");
        int iterations = BenchMode ? 500 : 50;
        byte[] payload = new byte[256];

        try
        {
            await using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            double[] ms = new double[iterations];
            for (int i = 0; i < iterations; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                await fs.WriteAsync(payload);
                await fs.FlushAsync();
                fs.Flush(true);
                ms[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            Array.Sort(ms);
            _output.WriteLine($"[Layer0 fsync] iterations={iterations} p50={Percentile(ms, 50):N3}ms p95={Percentile(ms, 95):N3}ms p99={Percentile(ms, 99):N3}ms => ceiling for a group-of-1 WAL ~= {1000.0 / Math.Max(Percentile(ms, 50), 1e-6):N0} appends/s");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ─── Layer 1: WAL append throughput + group-commit coalescing visibility ───

    [Fact]
    public async Task Layer1_WalAppendThroughput_AndCoalescing()
    {
        _output.WriteLine($"[Layer1 WAL] appends/level={Appends}, mode={(BenchMode ? "bench" : "smoke")}");
        _output.WriteLine("| concurrency | appends/s | p50 ms | p95 ms | p99 ms | avg group | groups | avg flush ms |");
        _output.WriteLine("|---|---|---|---|---|---|---|---|");

        foreach (int concurrency in ConcurrencyLevels)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"nealytics_wal_bench_{Guid.NewGuid():N}");
            int total = Appends;
            double[] latencies = new double[total];
            GlobalTelemetryPayload sample = SampleEvent();

            double throughput;
            double avgGroup, avgFlushMs;
            long groups;
            await using (WriteAheadLogger wal = new WriteAheadLogger(Options(dir)))
            {
                Stopwatch sw = Stopwatch.StartNew();
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, total),
                    new ParallelOptions { MaxDegreeOfParallelism = concurrency },
                    async (i, ct) =>
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        await wal.AppendAsync(sample, ct);
                        latencies[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    });
                sw.Stop();

                throughput = total / Math.Max(sw.Elapsed.TotalSeconds, 1e-9);
                avgGroup = wal.AverageGroupSize;
                avgFlushMs = wal.AverageFlushMilliseconds;
                groups = wal.FlushGroupCount;
                wal.UncommittedRecordCount.Should().Be(total, "every append must be counted durable");
            }

            Array.Sort(latencies);
            _output.WriteLine($"| {concurrency} | {throughput:N0} | {Percentile(latencies, 50):N3} | {Percentile(latencies, 95):N3} | {Percentile(latencies, 99):N3} | {avgGroup:N1} | {groups} | {avgFlushMs:N3} |");

            await using (WriteAheadLogger reopened = new WriteAheadLogger(Options(dir)))
            {
                IReadOnlyList<GlobalTelemetryPayload> recovered = await reopened.ReplayUncommittedAsync();
                recovered.Count.Should().Be(total, "zero-loss: every WAL append must survive as a durable record");
            }

            Directory.Delete(dir, true);
        }
    }

    // ─── Layer 2: in-memory channel publish throughput (should dwarf the WAL) ───

    [Fact]
    public async Task Layer2_ChannelPublishThroughput()
    {
        int total = Appends;
        TelemetryChannelBroker broker = new TelemetryChannelBroker(Options(Path.GetTempPath()));
        GlobalTelemetryPayload sample = SampleEvent();

        // Drain concurrently so a bounded channel never blocks the producers.
        Task drain = Task.Run(async () =>
        {
            int read = 0;
            while (read < total && await broker.Reader.WaitToReadAsync())
            {
                while (broker.Reader.TryRead(out _))
                {
                    read++;
                }
            }
        });

        Stopwatch sw = Stopwatch.StartNew();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, total),
            new ParallelOptions { MaxDegreeOfParallelism = 64 },
            async (i, ct) => await broker.PublishAsync(sample, ct));
        sw.Stop();
        await drain;

        double throughput = total / Math.Max(sw.Elapsed.TotalSeconds, 1e-9);
        _output.WriteLine($"[Layer2 channel] publish {total} events @ conc 64 => {throughput:N0} events/s (p50 negligible)");
        throughput.Should().BeGreaterThan(0);
    }
}

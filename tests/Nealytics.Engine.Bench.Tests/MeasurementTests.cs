using System.Text.Json;
using Nealytics.Engine.Bench.Measurements;

namespace Nealytics.Engine.Bench.Tests;

public sealed class MeasurementTests
{
    [Theory]
    [InlineData(0, 50, 0)]
    [InlineData(10, 0, 0)]
    [InlineData(10, 1, 100)]
    [InlineData(2.5, 3, 1200)]
    public void ScheduleUsesAbsoluteSequenceOffsets(double rate, long sequence, double expectedMilliseconds)
    {
        ArrivalSchedule schedule = new(rate);

        Assert.Equal(rate, schedule.RequestsPerSecond);
        Assert.Equal(rate > 0, schedule.IsOpenLoop);
        Assert.Equal(expectedMilliseconds, schedule.Offset(sequence).TotalMilliseconds);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    public void ScheduleRejectsInvalidRates(double rate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(rate));
    }

    [Fact]
    public void ScheduleRejectsNegativeSequenceEvenInClosedLoop()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(0).Offset(-1));
    }

    [Fact]
    public void ScheduleRejectsOffsetsOutsideTimeSpan()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(1).Offset(long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(double.Epsilon).Offset(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(TimeSpan.TicksPerSecond).Offset(long.MaxValue));
    }

    [Fact]
    public void EmptyHistogramIsExplicitlyEmpty()
    {
        LatencyHistogram histogram = new();

        Assert.Equal(0, histogram.Count);
        Assert.Equal(0, histogram.MaximumMilliseconds);
        Assert.Equal(0, histogram.Percentile(99));
    }

    [Fact]
    public void ZeroLatencySamplesRemainZero()
    {
        LatencyHistogram histogram = new();
        histogram.Record(0);
        histogram.Record(0);

        Assert.Equal(2, histogram.Count);
        Assert.Equal(0, histogram.Percentile(0));
        Assert.Equal(0, histogram.Percentile(100));
    }

    [Fact]
    public void HistogramPreservesNearestRankWithinItsQuantizationBound()
    {
        LatencyHistogram histogram = new();

        for (int value = 1; value <= 1000; value++)
        {
            histogram.Record(value);
        }

        Assert.Equal(1000, histogram.Count);
        Assert.InRange(histogram.MaximumMilliseconds, 1000, 1000 * (1 + 1.0 / 256));

        foreach (double percentile in new double[] { 0, 1, 50, 95, 99, 100 })
        {
            double exact = Math.Max(1, Math.Ceiling(1000 * percentile / 100));
            Assert.InRange(histogram.Percentile(percentile), exact, exact * (1 + 1.0 / 256) + 0.001);
        }
    }

    [Fact]
    public void HistogramHandlesDescendingAndSubMicrosecondSamples()
    {
        LatencyHistogram histogram = new();
        histogram.Record(1234.56789);
        histogram.Record(0.000001);
        histogram.Record(0);

        Assert.InRange(histogram.MaximumMilliseconds, 1234.568, 1234.568 * (1 + 1.0 / 256));
        Assert.Equal(0, histogram.Percentile(0));
        Assert.InRange(histogram.Percentile(50), 0.001, 0.002);
        Assert.Equal(histogram.MaximumMilliseconds, histogram.Percentile(100));
    }

    [Fact]
    public void HistogramAcceptsItsLargestSupportedValue()
    {
        LatencyHistogram histogram = new();
        double maximum = (1L << 40) / 1000.0;
        histogram.Record(maximum);

        Assert.Equal(maximum, histogram.Percentile(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Record(maximum + 0.001));
    }

    [Fact]
    public void HistogramUsesTheCorrectExponentAtSubBucketBoundaries()
    {
        LatencyHistogram histogram = new();
        histogram.Record(0.511);
        histogram.Record(10);

        Assert.Equal(0.512, histogram.Percentile(50));
        Assert.InRange(histogram.MaximumMilliseconds, 10, 10.04);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    public void HistogramRejectsInvalidMeasurements(double value)
    {
        LatencyHistogram histogram = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Record(value));
        Assert.Equal(0, histogram.Count);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void HistogramRejectsInvalidPercentiles(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyHistogram().Percentile(value));
    }

    [Fact]
    public void ConcurrentRecordersDoNotLoseSamples()
    {
        LatencyHistogram histogram = new();
        Parallel.For(0, 100000, value => histogram.Record(value % 100));

        Assert.Equal(100000, histogram.Count);
        Assert.InRange(histogram.MaximumMilliseconds, 99, 99.4);
        Assert.InRange(histogram.Percentile(95), 94, 94.5);
    }

    [Theory]
    [InlineData("http://user:secret@localhost:5000/path?k=secret#token", "http://localhost:5000/path")]
    [InlineData("https://localhost", "https://localhost/")]
    public void ManifestTargetDoesNotExposeCredentials(string source, string expected)
    {
        Assert.Equal(expected, BenchmarkManifest.SafeTarget(source));
    }

    [Fact]
    public void ManifestRejectsNonHttpTargets()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => BenchmarkManifest.SafeTarget("file:///tmp/target"));

        Assert.Contains("HTTP or HTTPS", exception.Message);
    }

    [Fact]
    public void ManifestSettingsUseAnExplicitAllowlist()
    {
        Dictionary<string, string> environment = new()
        {
            ["TelemetryEngine__DatabaseBatchCommitSize"] = "123",
            ["TelemetryEngine__WalBackend"] = "managed",
            ["TelemetryEngine__JwtSymmetricKey"] = "secret",
            ["TelemetryEngine__AllowedProjectKeys"] = "another-secret",
            ["TelemetryEngine__ClickHouseConnectionString"] = "Password=secret"
        };

        Dictionary<string, string> settings = BenchmarkManifest.CaptureSettings(
            key => environment.GetValueOrDefault(key));

        Assert.Equal(2, settings.Count);
        Assert.Equal("123", settings["DatabaseBatchCommitSize"]);
        Assert.Equal("managed", settings["WalBackend"]);
        Assert.DoesNotContain(settings.Keys, key => key.Contains("Key", StringComparison.Ordinal));
        Assert.Throws<ArgumentNullException>(() => BenchmarkManifest.CaptureSettings(null!));
    }

    [Fact]
    public void ManifestRetainsAllDeclaredPerformanceSettings()
    {
        string[] expected =
        [
            "DatabaseBatchCommitSize", "ForceFlushIntervalSeconds", "MemoryChannelCapacity",
            "RateLimitPermitCount", "RateLimitWindowSeconds", "RateLimitQueueSize",
            "ConnectionPoolSize", "WalFileBufferBytes", "EnableAsyncInsert", "EnableWireCompression",
            "WalBackend", "IngestionPipeline", "WalMaxPendingRecords", "WalMaxPendingBytes",
            "WalMaxRecordBytes", "WalMaxFlushRecords", "WalMaxFlushBytes"
        ];

        Dictionary<string, string> settings = BenchmarkManifest.CaptureSettings(key => key);

        Assert.Equal(expected.Order(), settings.Keys.Order());

        foreach (string key in expected)
        {
            Assert.Equal("TelemetryEngine__" + key, settings[key]);
        }
    }

    [Fact]
    public void EmptyManifestDoesNotInventRunEvidence()
    {
        BenchmarkManifest manifest = new();

        Assert.Equal(string.Empty, manifest.Revision);
        Assert.Equal(string.Empty, manifest.Target);
        Assert.Equal(string.Empty, manifest.Mode);
        Assert.Empty(manifest.Levels);
        Assert.Empty(manifest.DeclaredEngineSettings);
        Assert.Null(manifest.ClickHouseVersion);
        Assert.Equal(0, manifest.OfferedRequestsPerSecond);
        Assert.False(manifest.VerifyStoredCount);
    }

    [Fact]
    public void ManifestSerializesOnlyTheEvidenceModel()
    {
        BenchmarkManifest manifest = new()
        {
            Revision = "revision",
            Target = "https://localhost/",
            Mode = "track",
            OfferedRequestsPerSecond = 25,
            Levels = [new LevelResult { Ok = 4, Stored = 4, EventsSent = 4, VisibilitySamples = 0 }]
        };

        string json = JsonSerializer.Serialize(manifest, BenchmarkJsonContext.Default.BenchmarkManifest);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("revision", root.GetProperty("revision").GetString());
        Assert.Equal(25, root.GetProperty("offeredRequestsPerSecond").GetDouble());
        Assert.True(root.GetProperty("clientProcessorCount").GetInt32() > 0);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("levels")[0].GetProperty("visibilityP95").ValueKind);
        Assert.DoesNotContain("ProjectKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JwtKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 0, 4, 4, true, true)]
    [InlineData(0, 0, 3, 4, true, false)]
    [InlineData(0, 0, 5, 4, true, false)]
    [InlineData(0, 0, -1, 4, true, false)]
    [InlineData(0, 0, -1, 4, false, true)]
    [InlineData(1, 0, 4, 4, false, false)]
    [InlineData(0, 1, 4, 4, false, false)]
    public void VerificationRejectsLossDuplicatesUnavailableCountsAndProbeTimeouts(
        long errors, long timeouts, long stored, long accepted, bool verify, bool expected)
    {
        LevelResult result = new()
        {
            Errors = errors,
            VisibilityTimeouts = timeouts,
            Stored = stored,
            EventsSent = accepted
        };

        Assert.Equal(expected, BenchmarkVerification.Passed(result, verify));
        Assert.Throws<ArgumentNullException>(() => BenchmarkVerification.Passed(null!, verify));
    }
}

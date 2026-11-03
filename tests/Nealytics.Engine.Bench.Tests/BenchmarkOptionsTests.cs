namespace Nealytics.Engine.Bench.Tests;

public sealed class BenchmarkOptionsTests
{
    [Fact]
    public void AdditiveMeasurementOptionsDoNotChangeLegacyDefaults()
    {
        BenchOptions options = BenchOptions.Parse([]);

        Assert.Equal("track", options.Mode);
        Assert.Equal(40000, options.Requests);
        Assert.Equal(0, options.Duration);
        Assert.Equal(3000, options.Warmup);
        Assert.Equal(50, options.BeaconBatch);
        Assert.True(options.VerifyLoss);
        Assert.True(options.IsWriteMode);
        Assert.False(options.NeedsJwt);
        Assert.Null(options.ManifestFile);
        Assert.Equal(0, options.OfferedRate);
        Assert.Equal(0, options.VisibilityEvery);
    }

    [Fact]
    public void ExplicitMeasurementAndExistingOptionsAreParsedTogether()
    {
        BenchOptions options = BenchOptions.Parse(
        [
            "--url", "https://localhost:5000/",
            "--mode", "beacon",
            "--key", "write-key",
            "--jwt-key", "read-key",
            "--concurrency", "1, 4,16",
            "--requests", "200",
            "--duration", "30.5",
            "--warmup", "0",
            "--beacon-batch", "20",
            "--declared", "true",
            "--seed-tenant", "seed",
            "--verify-loss", "false",
            "--ch", "Host=localhost;",
            "--out", "results.md",
            "--metrics-url", "https://localhost/metrics",
            "--read-tenant", "reader",
            "--read-set", "parent:group",
            "--manifest", "manifest.json",
            "--rate", "10.5",
            "--visibility-every", "2"
        ]);

        Assert.Equal("https://localhost:5000/", options.BaseUrl);
        Assert.Equal("write-key", options.ProjectKey);
        Assert.Equal("read-key", options.JwtKey);
        Assert.Equal([1, 4, 16], options.Concurrency);
        Assert.Equal(200, options.Requests);
        Assert.Equal(30.5, options.Duration);
        Assert.Equal(0, options.Warmup);
        Assert.Equal(20, options.BeaconBatch);
        Assert.True(options.Declared);
        Assert.Equal("seed", options.SeedTenant);
        Assert.False(options.VerifyLoss);
        Assert.Equal("Host=localhost;", options.ClickHouseConnectionString);
        Assert.Equal("results.md", options.OutFile);
        Assert.Equal("https://localhost/metrics", options.MetricsUrl);
        Assert.Equal("reader", options.ReadTenant);
        Assert.Equal("parent:group", options.ReadSet);
        Assert.Equal("manifest.json", options.ManifestFile);
        Assert.Equal(10.5, options.OfferedRate);
        Assert.Equal(2, options.VisibilityEvery);
        Assert.Contains("duration=30.5s/level", options.Describe());
        Assert.Contains("beaconBatch=20", options.Describe());
        Assert.DoesNotContain("write-key", options.Describe());
        Assert.DoesNotContain("read-key", options.Describe());
    }

    [Theory]
    [InlineData("noop", false, false)]
    [InlineData("track", true, false)]
    [InlineData("beacon", true, false)]
    [InlineData("timeline", false, true)]
    public void ModeDeterminesWhetherCredentialsAndCountChecksAreNeeded(
        string mode, bool writes, bool jwt)
    {
        BenchOptions options = BenchOptions.Parse(["--mode", mode]);

        Assert.Equal(writes, options.IsWriteMode);
        Assert.Equal(jwt, options.NeedsJwt);
        Assert.Contains("requests=40000/level", options.Describe());
    }

    [Theory]
    [InlineData("--concurrency", "")]
    [InlineData("--concurrency", "0")]
    [InlineData("--concurrency", "-1")]
    [InlineData("--requests", "0")]
    [InlineData("--duration", "-1")]
    [InlineData("--duration", "NaN")]
    [InlineData("--duration", "Infinity")]
    [InlineData("--warmup", "-1")]
    [InlineData("--beacon-batch", "0")]
    [InlineData("--visibility-every", "-1")]
    [InlineData("--rate", "-1")]
    [InlineData("--rate", "NaN")]
    public void InvalidWorkloadLimitsFailBeforeSendingRequests(string key, string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => BenchOptions.Parse([key, value]));
    }

    [Fact]
    public void IncompleteOptionPairsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(["--rate"]));
    }

    [Fact]
    public void EmptyOptionalValuesStayAbsent()
    {
        BenchOptions options = BenchOptions.Parse(
        [
            "--seed-tenant", "", "--metrics-url", "", "--read-tenant", "",
            "--read-set", "", "--manifest", ""
        ]);

        Assert.Null(options.SeedTenant);
        Assert.Null(options.MetricsUrl);
        Assert.Null(options.ReadTenant);
        Assert.Null(options.ReadSet);
        Assert.Null(options.ManifestFile);
    }

    [Fact]
    public void ConsoleEvidenceIncludesTheMeasuredValues()
    {
        LevelResult result = new()
        {
            Ok = 20,
            Errors = 2,
            Throughput = 10,
            EventThroughput = 30,
            P50 = 1,
            P95 = 2,
            P99 = 3,
            Max = 4,
            Stored = 60,
            EventsSent = 60
        };

        string line = result.ConsoleLine();

        Assert.Contains("ok=20 err=2", line);
        Assert.Contains("stored=60/60", line);
        Assert.DoesNotContain("stored=", new LevelResult { Stored = -1 }.ConsoleLine());
    }
}

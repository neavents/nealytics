using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nealytics.Engine.Tests.Integration;

// Isolated factory that relaxes the rate limiter (via in-memory config, so it does not
// pollute the process environment for other factories) and tightens the flush cadence so a
// burst commits quickly. Used only by the load / zero-loss-under-concurrency guard below.
public sealed class LoadTestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TelemetryEngine:JwtSymmetricKey"] = "this-is-a-test-jwt-key-at-least-32-bytes-long!!",
                ["TelemetryEngine:AllowedProjectKeys"] = "test-key-1,test-key-2",
                ["TelemetryEngine:ClickHouseConnectionString"] = ClickHouseTestSupport.ConnectionString,
                ["TelemetryEngine:WriteAheadLogDirectory"] = Path.Combine(Path.GetTempPath(), $"nealytics_wal_load_{Guid.NewGuid():N}"),
                ["TelemetryEngine:RateLimitPermitCount"] = "100000000",
                ["TelemetryEngine:RateLimitWindowSeconds"] = "1",
                ["TelemetryEngine:RateLimitQueueSize"] = "1000000",
                ["TelemetryEngine:MemoryChannelCapacity"] = "200000",
                ["TelemetryEngine:DatabaseBatchCommitSize"] = "5000",
                ["TelemetryEngine:ForceFlushIntervalSeconds"] = "1"
            });
        });

        builder.UseEnvironment("Testing");
    }
}

[Collection("ClickHouse")]
public class LoadTests : IClassFixture<LoadTestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public LoadTests(LoadTestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    // Scale up locally with NEALYTICS_LOAD_EVENTS to exercise heavier bursts.
    private static int EventCount =>
        int.TryParse(Environment.GetEnvironmentVariable("NEALYTICS_LOAD_EVENTS"), out int n) ? n : 2000;

    private const int Concurrency = 64;

    [Fact]
    public async Task ZeroLoss_UnderConcurrentBurst_EveryAcceptedEventIsPersisted()
    {
        int total = EventCount;
        string projectId = $"p-load-{Guid.NewGuid():N}";
        string tenantId = "t-load";

        ConcurrentBag<HttpStatusCode> statuses = new ConcurrentBag<HttpStatusCode>();

        Stopwatch sw = Stopwatch.StartNew();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, total),
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency },
            async (i, ct) =>
            {
                var payload = new
                {
                    projectId,
                    tenantId,
                    sessionId = $"s{i}",
                    eventType = "load",
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    metadataJson = "{}"
                };
                HttpResponseMessage response = await _client.PostAsJsonAsync(
                    "/api/v1/telemetry/track?k=test-key-1", payload, ct);
                statuses.Add(response.StatusCode);
            });
        sw.Stop();

        int accepted = statuses.Count(s => s == HttpStatusCode.Accepted);
        accepted.Should().Be(total, "every request must be accepted when the rate limiter is relaxed");

        long stored = 0;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            stored = await ClickHouseTestSupport.CountAsync(projectId);
            if (stored >= accepted)
            {
                break;
            }
            await Task.Delay(1000);
        }

        stored.Should().Be(accepted,
            "every accepted (202) event must be durably persisted — zero data loss under concurrent load");

        double throughput = accepted / Math.Max(sw.Elapsed.TotalSeconds, 1e-9);
        Console.WriteLine($"[load] {accepted} events @ concurrency {Concurrency} in {sw.Elapsed.TotalSeconds:N2}s => {throughput:N0} req/s (in-process TestServer)");
    }
}

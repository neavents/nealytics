using System.Net.Http.Json;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class BatchProcessorFlushTests : IntegrationTestBase
{
    public BatchProcessorFlushTests(TestWebApplicationFactory factory) : base(factory) { }

    private static async Task<int> QueryClickHouseCount(string projectId)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker",
            $"exec nealytics-ch clickhouse-client -q \"SELECT count() FROM nealytics_core.global_events WHERE project_id='{projectId}'\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync();
        var output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
        int.TryParse(output, out var count);
        return count;
    }

    [Theory]
    [InlineData(7)]
    [InlineData(3)]
    public async Task IngestEvents_NormalFlush_CommitsToClickHouse(int eventCount)
    {
        var projectId = $"p-flush-{eventCount}";
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");
        for (int i = 0; i < eventCount; i++)
        {
            var payload = new
            {
                projectId,
                tenantId = "t-flush",
                sessionId = "s-flush",
                eventType = $"flush_{i}"
            };
            await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload);
        }
        Client.DefaultRequestHeaders.Remove("X-Project-Key");

        await Task.Delay(4000);

        var count = await QueryClickHouseCount(projectId);
        count.Should().Be(eventCount, "all ingested events should be flushed to ClickHouse");
    }
}

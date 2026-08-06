using System.Net.Http.Json;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class BatchProcessorFlushTests : IntegrationTestBase
{
    public BatchProcessorFlushTests(TestWebApplicationFactory factory) : base(factory) { }

    [Theory]
    [InlineData(7)]
    [InlineData(3)]
    public async Task IngestEvents_NormalFlush_CommitsToClickHouse(int eventCount)
    {
        var projectId = $"p-flush-{eventCount}";

        // This class asserts an exact count under a project id that is the same on every run, so it
        // has to start from a known state. It never cleaned up: it was relying on another class's
        // `TRUNCATE TABLE global_events` running first and emptying the whole table — which also
        // destroyed the estate's real analytics. With the truncate gone this failed honestly on the
        // second run ("expected 3, found 6"), which is what a test with no cleanup should always
        // have done.
        await ClickHouseTestSupport.DeleteProjectsAsync(projectId);

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

        long count = await ClickHouseTestSupport.CountAsync(projectId);
        count.Should().Be(eventCount, "all ingested events should be flushed to ClickHouse");
    }
}

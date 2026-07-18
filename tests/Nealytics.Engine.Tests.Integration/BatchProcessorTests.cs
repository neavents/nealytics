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

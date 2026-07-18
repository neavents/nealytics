using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class DedupIntegrationTests : IntegrationTestBase, IAsyncLifetime
{
    public DedupIntegrationTests(TestWebApplicationFactory factory) : base(factory) { }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task WalReplayDuplicates_CollapseToSingleRow_AfterMerge()
    {
        string projectId = $"p-dedup-{Guid.NewGuid():N}";
        string fixedTimestamp = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        Guid[] eventIds =
        {
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
        };

        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");

        for (int round = 0; round < 2; round++)
        {
            foreach (Guid eventId in eventIds)
            {
                var payload = new
                {
                    eventId,
                    projectId,
                    tenantId = "t-dedup",
                    sessionId = "s-dedup",
                    eventType = "dup_event",
                    timestamp = fixedTimestamp,
                    metadataJson = "{}"
                };
                HttpResponseMessage response = await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload);
                response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            }
        }

        Client.DefaultRequestHeaders.Remove("X-Project-Key");

        await Task.Delay(4000);

        await ClickHouseTestSupport.OptimizeFinalAsync();

        long count = await ClickHouseTestSupport.CountAsync(projectId);
        count.Should().Be(eventIds.Length,
            "ReplacingMergeTree must collapse rows sharing the same event_id so replayed batches are idempotent");
    }
}

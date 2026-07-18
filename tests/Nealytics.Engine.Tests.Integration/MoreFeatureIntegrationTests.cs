using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class MoreFeatureIntegrationTests : IntegrationTestBase, IAsyncLifetime
{
    public MoreFeatureIntegrationTests(TestWebApplicationFactory factory) : base(factory) { }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private static string RecentTimestamp(int minutesAgo) =>
        DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private async Task Ingest(string projectId, string tenantId, string sessionId, string eventType,
        string? itemId = null, string? timestamp = null)
    {
        var payload = new
        {
            projectId,
            tenantId,
            sessionId,
            eventType,
            itemId,
            timestamp = timestamp ?? RecentTimestamp(1),
            metadataJson = "{}"
        };
        HttpResponseMessage response = await Client.PostAsJsonAsync("/api/v1/telemetry/track?k=test-key-1", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private async Task<JsonElement> GetJson(string url, string projectId, string tenantId)
    {
        string jwt = GetJwt(projectId, tenantId);
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        HttpResponseMessage response = await Client.GetAsync(url);
        Client.DefaultRequestHeaders.Remove("Authorization");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Beacon_MultipleEvents_AllPersisted()
    {
        string projectId = $"p-beacon-{Guid.NewGuid():N}";
        var payload = new[]
        {
            new { projectId, tenantId = "t", sessionId = "s", eventType = "load", timestamp = RecentTimestamp(1), metadataJson = "{}" },
            new { projectId, tenantId = "t", sessionId = "s", eventType = "scroll", timestamp = RecentTimestamp(1), metadataJson = "{}" },
            new { projectId, tenantId = "t", sessionId = "s", eventType = "unload", timestamp = RecentTimestamp(1), metadataJson = "{}" }
        };

        HttpResponseMessage response = await Client.PostAsJsonAsync("/api/v1/telemetry/beacon?k=test-key-1", payload);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await Task.Delay(4000);
        (await ClickHouseTestSupport.CountAsync(projectId)).Should().Be(3);
    }

    [Fact]
    public async Task Track_HeaderAndQueryKeyBothPresent_Accepted()
    {
        var payload = new { projectId = "p-both", tenantId = "t", sessionId = "s", eventType = "e" };
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-2");
        HttpResponseMessage response = await Client.PostAsJsonAsync("/api/v1/telemetry/track?k=test-key-1", payload);
        Client.DefaultRequestHeaders.Remove("X-Project-Key");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Track_BodyExceedingLimit_Returns413()
    {
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");
        string big = new string('x', 2_000_000);
        string json = "{\"projectId\":\"p\",\"tenantId\":\"t\",\"sessionId\":\"s\",\"eventType\":\"e\",\"metadataJson\":\"" + big + "\"}";
        StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await Client.PostAsync("/api/v1/telemetry/track", content);
        Client.DefaultRequestHeaders.Remove("X-Project-Key");
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Timeline_ItemIdFilter_ReturnsOnlyMatching()
    {
        string tenantId = $"t-item-{Guid.NewGuid():N}";
        await Ingest("p-item", tenantId, "s", "click", itemId: "/pricing");
        await Ingest("p-item", tenantId, "s", "click", itemId: "/home");
        await Task.Delay(4000);

        JsonElement body = await GetJson("/api/v1/telemetry/timeline?limit=50&itemId=/pricing", "p-item", tenantId);
        JsonElement events = body.GetProperty("events");
        events.GetArrayLength().Should().Be(1);
        events[0].GetProperty("itemId").GetString().Should().Be("/pricing");
    }

    [Fact]
    public async Task Timeline_CursorPagination_WalksBackwards()
    {
        string tenantId = $"t-page-{Guid.NewGuid():N}";
        await Ingest("p-page", tenantId, "s", "e1", timestamp: RecentTimestamp(3));
        await Ingest("p-page", tenantId, "s", "e2", timestamp: RecentTimestamp(2));
        await Ingest("p-page", tenantId, "s", "e3", timestamp: RecentTimestamp(1));
        await Task.Delay(4000);

        JsonElement page1 = await GetJson("/api/v1/telemetry/timeline?limit=2", "p-page", tenantId);
        JsonElement events1 = page1.GetProperty("events");
        events1.GetArrayLength().Should().Be(2, "first page returns the two newest events");
        string oldestOnPage1 = events1[1].GetProperty("timestamp").GetString()!;

        JsonElement page2 = await GetJson($"/api/v1/telemetry/timeline?limit=2&before={oldestOnPage1}", "p-page", tenantId);
        JsonElement events2 = page2.GetProperty("events");
        events2.GetArrayLength().Should().Be(1, "one older event remains before the cursor");
        events2[0].GetProperty("eventType").GetString().Should().Be("e1");
    }

    [Fact]
    public async Task TimeSeries_CrossTenant_IsolatedByJwt()
    {
        string tenantA = $"t-a-{Guid.NewGuid():N}";
        string tenantB = $"t-b-{Guid.NewGuid():N}";
        await Ingest("p-tsiso", tenantA, "s", "hit");
        await Ingest("p-tsiso", tenantA, "s", "hit");
        await Ingest("p-tsiso", tenantB, "s", "hit");
        await Task.Delay(4000);

        JsonElement bodyA = await GetJson(
            "/api/v1/analytics/timeseries?from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z&interval=day", "p-tsiso", tenantA);
        bodyA.GetProperty("totalCount").GetInt64().Should().Be(2, "tenant A only sees its own events");
    }

    [Fact]
    public async Task Sessions_CrossTenant_IsolatedByJwt()
    {
        string tenantA = $"t-sa-{Guid.NewGuid():N}";
        string tenantB = $"t-sb-{Guid.NewGuid():N}";
        await Ingest("p-siso", tenantA, "sess-1", "x");
        await Ingest("p-siso", tenantB, "sess-2", "x");
        await Task.Delay(4000);

        JsonElement bodyA = await GetJson(
            "/api/v1/analytics/sessions?from=2020-01-01T00:00:00Z&to=2030-01-01T00:00:00Z", "p-siso", tenantA);
        bodyA.GetProperty("uniqueSessionCount").GetInt32().Should().Be(1, "tenant A only sees its own session");
    }

    [Fact]
    public async Task TimeSeries_MinuteInterval_ReturnsBuckets()
    {
        string tenantId = $"t-min-{Guid.NewGuid():N}";
        await Ingest("p-min", tenantId, "s", "hit");
        await Ingest("p-min", tenantId, "s", "hit");
        await Task.Delay(4000);

        JsonElement body = await GetJson(
            "/api/v1/analytics/timeseries?from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z&interval=minute", "p-min", tenantId);
        body.GetProperty("interval").GetString().Should().Be("minute");
        body.GetProperty("totalCount").GetInt64().Should().Be(2);
        body.GetProperty("points").GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Dedup_DistinctEventIds_AreNotCollapsed()
    {
        string projectId = $"p-nodup-{Guid.NewGuid():N}";
        string ts = RecentTimestamp(1);
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");
        for (int i = 0; i < 4; i++)
        {
            var payload = new
            {
                eventId = Guid.NewGuid(),
                projectId,
                tenantId = "t",
                sessionId = "s",
                eventType = "distinct",
                timestamp = ts,
                metadataJson = "{}"
            };
            (await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload)).StatusCode
                .Should().Be(HttpStatusCode.Accepted);
        }
        Client.DefaultRequestHeaders.Remove("X-Project-Key");

        await Task.Delay(4000);
        await ClickHouseTestSupport.OptimizeFinalAsync();
        (await ClickHouseTestSupport.CountAsync(projectId)).Should().Be(4, "distinct event_ids must never be collapsed");
    }

    [Fact]
    public async Task Ttl_EventsOlderThanRetention_AreEvictedOnMerge()
    {
        string projectId = $"p-ttl-{Guid.NewGuid():N}";
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");

        var oldEvent = new
        {
            projectId,
            tenantId = "t",
            sessionId = "s",
            eventType = "ancient",
            timestamp = DateTime.UtcNow.AddDays(-200).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            metadataJson = "{}"
        };
        var recentEvent = new
        {
            projectId,
            tenantId = "t",
            sessionId = "s",
            eventType = "fresh",
            timestamp = RecentTimestamp(1),
            metadataJson = "{}"
        };
        (await Client.PostAsJsonAsync("/api/v1/telemetry/track", oldEvent)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Client.PostAsJsonAsync("/api/v1/telemetry/track", recentEvent)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        Client.DefaultRequestHeaders.Remove("X-Project-Key");

        await Task.Delay(4000);
        await ClickHouseTestSupport.OptimizeFinalAsync();

        (await ClickHouseTestSupport.CountAsync(projectId)).Should().Be(1,
            "the 200-day-old event must be evicted by the 90-day TTL, leaving only the recent one");
    }

    [Fact]
    public async Task Beacon_BodyExceedingLimit_Returns413()
    {
        string big = new string('y', 2_000_000);
        string json = "[{\"projectId\":\"p\",\"tenantId\":\"t\",\"sessionId\":\"s\",\"eventType\":\"e\",\"metadataJson\":\"" + big + "\"}]";
        StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await Client.PostAsync("/api/v1/telemetry/beacon?k=test-key-1", content);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Beacon_MalformedJson_Returns400()
    {
        StringContent content = new StringContent("[{bad json", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await Client.PostAsync("/api/v1/telemetry/beacon?k=test-key-1", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Timeline_UnicodeFilterValue_IsHandled()
    {
        string tenantId = $"t-uni-{Guid.NewGuid():N}";
        await Ingest("p-uni", tenantId, "s", "café_view");
        await Task.Delay(4000);

        JsonElement body = await GetJson("/api/v1/telemetry/timeline?limit=50&eventType=café_view", "p-uni", tenantId);
        body.GetProperty("events").GetArrayLength().Should().Be(1);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class SeededE2ETests : IntegrationTestBase, IAsyncLifetime
{
    public SeededE2ETests(TestWebApplicationFactory factory) : base(factory) { }

    public async Task InitializeAsync()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker", "exec nealytics-ch clickhouse-client -q \"TRUNCATE TABLE nealytics_core.global_events\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
    [Fact]
    public async Task WriteEvents_Flush_QueryTimeline_ReturnsCorrectData()
    {
        // ── Ingest 5 events across 2 sessions ──
        var jwt = GetJwt("p1", "t1");
        var apiKey = "test-key-1";
        Client.DefaultRequestHeaders.Add("X-Project-Key", apiKey);

        for (int i = 0; i < 3; i++)
        {
            var payload = new
            {
                projectId = "p1",
                tenantId = "t1",
                sessionId = "s-e2e-1",
                eventType = $"e2e_event_{i}",
                itemId = $"/page/{i}",
                metadataJson = $"{{\"seq\":{i}}}"
            };
            var resp = await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload);
            resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        for (int i = 0; i < 2; i++)
        {
            var payload = new
            {
                projectId = "p1",
                tenantId = "t1",
                sessionId = "s-e2e-2",
                eventType = $"e2e_other_{i}",
                itemId = (string?)null,
                metadataJson = "{}"
            };
            var resp = await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload);
            resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        Client.DefaultRequestHeaders.Remove("X-Project-Key");

        // ── Wait for batch flush (ForceFlushIntervalSeconds=1 in test config) ──
        await Task.Delay(4000);

        // ── Query timeline ──
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");

        var timelineResp = await Client.GetAsync("/api/v1/telemetry/timeline?limit=50");
        timelineResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var timeline = await timelineResp.Content.ReadFromJsonAsync<JsonElement>();
        var events = timeline.GetProperty("events");
        var eventCount = events.GetArrayLength();
        eventCount.Should().Be(5, "all 5 ingested events should be returned");

        timeline.GetProperty("projectId").GetString().Should().Be("p1");
        timeline.GetProperty("tenantId").GetString().Should().Be("t1");

        // Each event should have required fields
        var sampleEvent = events[0];
        sampleEvent.GetProperty("eventId").GetGuid().Should().NotBe(Guid.Empty);
        sampleEvent.GetProperty("sessionId").GetString().Should().NotBeNullOrEmpty();
        sampleEvent.GetProperty("eventType").GetString().Should().NotBeNullOrEmpty();
        sampleEvent.GetProperty("timestamp").GetString().Should().NotBeNullOrEmpty();

        // ── Test cursor pagination ──
        var oldestTs = events[eventCount - 1].GetProperty("timestamp").GetString();
        var cursorResp = await Client.GetAsync(
            $"/api/v1/telemetry/timeline?limit=50&before={oldestTs}");
        cursorResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var cursorTimeline = await cursorResp.Content.ReadFromJsonAsync<JsonElement>();
        cursorTimeline.GetProperty("events").GetArrayLength()
            .Should().Be(0, "no events should exist before the oldest event");

        // ── Query session analytics ──
        var sessResp = await Client.GetAsync(
            "/api/v1/analytics/sessions?from=2020-01-01T00:00:00Z&to=2030-01-01T00:00:00Z");
        sessResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var sessions = await sessResp.Content.ReadFromJsonAsync<JsonElement>();
        sessions.GetProperty("uniqueSessionCount").GetInt32().Should().Be(2);
        sessions.GetProperty("totalEventCount").GetInt64().Should().Be(5);

        var sessList = sessions.GetProperty("sessions");
        sessList.GetArrayLength().Should().Be(2);

        var s1 = sessList[0];
        s1.GetProperty("sessionId").GetString().Should().BeOneOf("s-e2e-1", "s-e2e-2");
        s1.GetProperty("eventCount").GetInt64().Should().BeOneOf(2, 3);

        var s2 = sessList[1];
        s2.GetProperty("eventCount").GetInt64().Should().BeOneOf(2, 3);
        s1.GetProperty("eventCount").GetInt64()
            .Should().NotBe(s2.GetProperty("eventCount").GetInt64(), "sessions have different counts");

        // Duration should be ≥ 0
        s1.GetProperty("durationSeconds").GetDouble().Should().BeGreaterThanOrEqualTo(0);

        // firstSeen and lastSeen should be valid timestamps
        s1.GetProperty("firstSeen").GetString().Should().NotBeNullOrEmpty();
        s1.GetProperty("lastSeen").GetString().Should().NotBeNullOrEmpty();

        Client.DefaultRequestHeaders.Remove("Authorization");
    }

    [Fact]
    public async Task WriteEvents_CrossTenant_IsolationIsRespected()
    {
        var apiKey = "test-key-1";
        Client.DefaultRequestHeaders.Add("X-Project-Key", apiKey);

        // Write for tenant t1
        var p1 = new { projectId = "p1", tenantId = "t1", sessionId = "s-iso", eventType = "iso_event" };
        await Client.PostAsJsonAsync("/api/v1/telemetry/track", p1);

        Client.DefaultRequestHeaders.Remove("X-Project-Key");
        await Task.Delay(4000);

        // Query as other-tenant
        var otherJwt = GetJwt("p1", "other-tenant");
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {otherJwt}");

        var resp = await Client.GetAsync("/api/v1/telemetry/timeline?limit=50");
        var timeline = await resp.Content.ReadFromJsonAsync<JsonElement>();
        timeline.GetProperty("events").GetArrayLength().Should().Be(0, "tenant isolation prevents cross-tenant reads");

        Client.DefaultRequestHeaders.Remove("Authorization");
    }
}

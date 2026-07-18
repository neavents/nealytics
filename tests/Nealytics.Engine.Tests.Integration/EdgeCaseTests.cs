using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class EdgeCaseTests : IntegrationTestBase, IAsyncLifetime
{
    public EdgeCaseTests(TestWebApplicationFactory factory) : base(factory) { }

    public Task InitializeAsync() => ClickHouseTestSupport.TruncateEventsAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task POST_Track_WithUnicodeMetadata_Returns202()
    {
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");
        var payload = new
        {
            projectId = "p-uni",
            tenantId = "t-uni",
            sessionId = "s-uni",
            eventType = "unicode_test",
            metadataJson = "{\"name\":\"İstanbul Café 🎉\",\"emoji\":\"💯\"}"
        };
        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload);
        Client.DefaultRequestHeaders.Remove("X-Project-Key");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task POST_Track_WithSpecialCharEventType_Returns202()
    {
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");
        var specialTypes = new[] { "page.view", "scroll_depth_50%", "click#button", "form:submit", "nav/home" };
        foreach (var et in specialTypes)
        {
            var payload = new { projectId = "p-special", tenantId = "t-special", sessionId = "s-special", eventType = et };
            var response = await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted, $"eventType '{et}' should be accepted");
        }
        Client.DefaultRequestHeaders.Remove("X-Project-Key");
    }

    [Fact]
    public async Task POST_Track_WithoutItemId_Returns202()
    {
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");
        var payload = new { projectId = "p-noitem", tenantId = "t-noitem", sessionId = "s-noitem", eventType = "no_item" };
        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload);
        Client.DefaultRequestHeaders.Remove("X-Project-Key");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task POST_Track_WithMaxLengthFields_Returns202()
    {
        var longId = new string('a', 200);
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");
        var payload = new { projectId = longId, tenantId = longId, sessionId = longId, eventType = longId };
        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload);
        Client.DefaultRequestHeaders.Remove("X-Project-Key");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task POST_Track_ConcurrentIngestion_AllAccepted()
    {
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");
            var payload = new { projectId = "p-conc", tenantId = "t-conc", sessionId = $"s-conc-{i % 5}", eventType = $"concurrent_{i}" };
            var resp = await client.PostAsJsonAsync("/api/v1/telemetry/track", payload);
            client.Dispose();
            return resp.StatusCode;
        });
        var results = await Task.WhenAll(tasks);
        results.Should().AllBeEquivalentTo(HttpStatusCode.Accepted, "all concurrent requests should be accepted");
    }

    [Fact]
    public async Task POST_Beacon_WithMixedValidInvalid_SkipsInvalid()
    {
        var payload = new object[]
        {
            new { projectId = "p-mix", tenantId = "t-mix", sessionId = "s-mix", eventType = "valid" },
            new { projectId = "", tenantId = "", sessionId = "", eventType = "" },
            new { projectId = "p-mix", tenantId = "t-mix", sessionId = "s-mix", eventType = "also_valid" }
        };
        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/beacon?k=test-key-1", payload);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task GET_Timeline_WithLimitZero_ClampsToOne()
    {
        var jwt = GetJwt("p1", "t1");
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        var response = await Client.GetAsync("/api/v1/telemetry/timeline?limit=0");
        Client.DefaultRequestHeaders.Remove("Authorization");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GET_Timeline_WithExcessiveLimit_ClampsToMax()
    {
        var jwt = GetJwt("p1", "t1");
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        var response = await Client.GetAsync("/api/v1/telemetry/timeline?limit=999999");
        Client.DefaultRequestHeaders.Remove("Authorization");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GET_Sessions_WithDefaultDateRange_Returns200()
    {
        var jwt = GetJwt("p1", "t1");
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        var response = await Client.GetAsync("/api/v1/analytics/sessions");
        Client.DefaultRequestHeaders.Remove("Authorization");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"sessions\":");
    }
}

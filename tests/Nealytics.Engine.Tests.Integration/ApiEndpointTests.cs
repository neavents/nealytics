using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class ApiEndpointTests : IntegrationTestBase
{
    public ApiEndpointTests(TestWebApplicationFactory factory) : base(factory) { }

    // ──────── HEALTH ────────

    [Fact]
    public async Task GET_Health_Returns200()
    {
        var response = await Client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GET_Ready_WhenClickHouseReachable_Returns200()
    {
        var response = await Client.GetAsync("/ready");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ──────── INGEST TRACK ────────

    [Fact]
    public async Task POST_Track_WithValidPayload_Returns202()
    {
        var payload = new
        {
            projectId = "p1",
            tenantId = "t1",
            sessionId = "s1",
            eventType = "test",
            itemId = "/path",
            metadataJson = "{\"x\":1}"
        };
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");

        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload);
        Client.DefaultRequestHeaders.Remove("X-Project-Key");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task POST_Track_WithQueryParamKey_Returns202()
    {
        var payload = new
        {
            projectId = "p1",
            tenantId = "t1",
            sessionId = "s1",
            eventType = "test2"
        };

        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/track?k=test-key-1", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task POST_Track_WithoutApiKey_Returns401()
    {
        var payload = new { projectId = "p", tenantId = "t", sessionId = "s", eventType = "e" };

        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task POST_Track_WithInvalidApiKey_Returns401()
    {
        var payload = new { projectId = "p", tenantId = "t", sessionId = "s", eventType = "e" };
        Client.DefaultRequestHeaders.Add("X-Project-Key", "wrong-key");

        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload);
        Client.DefaultRequestHeaders.Remove("X-Project-Key");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task POST_Track_WithEmptyBody_Returns400()
    {
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");

        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/track", new { });
        Client.DefaultRequestHeaders.Remove("X-Project-Key");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_Track_WithEmptyFields_Returns400()
    {
        var payload = new { projectId = "", tenantId = "", sessionId = "", eventType = "" };
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");

        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/track", payload);
        Client.DefaultRequestHeaders.Remove("X-Project-Key");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_Track_WithMalformedJson_Returns400()
    {
        Client.DefaultRequestHeaders.Add("X-Project-Key", "test-key-1");

        var content = new StringContent("not-json", System.Text.Encoding.UTF8, "application/json");
        var response = await Client.PostAsync("/api/v1/telemetry/track", content);
        Client.DefaultRequestHeaders.Remove("X-Project-Key");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ──────── BEACON ────────

    [Fact]
    public async Task POST_Beacon_WithValidArray_Returns204()
    {
        var payload = new[]
        {
            new { projectId = "p1", tenantId = "t1", sessionId = "s5", eventType = "load" },
            new { projectId = "p1", tenantId = "t1", sessionId = "s5", eventType = "visible" }
        };

        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/beacon?k=test-key-1", payload);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task POST_Beacon_WithoutApiKey_Returns401()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/beacon", new object[] { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task POST_Beacon_WithEmptyArray_Returns204()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/telemetry/beacon?k=test-key-1", new object[] { });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ──────── TIMELINE ────────

    [Fact]
    public async Task GET_Timeline_WithValidJwt_Returns200_AndValidShape()
    {
        var jwt = GetJwt("p1", "t1");

        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        var response = await Client.GetAsync("/api/v1/telemetry/timeline?limit=5");
        Client.DefaultRequestHeaders.Remove("Authorization");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"projectId\"");
        body.Should().Contain("\"tenantId\"");
        body.Should().Contain("\"events\"");
    }
    [Fact]
    public async Task GET_Timeline_WithoutJwt_Returns401()
    {
        var response = await Client.GetAsync("/api/v1/telemetry/timeline");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GET_Timeline_WithCrossTenantJwt_ReturnsEmpty()
    {
        var jwt = GetJwt("p1", "other-tenant");

        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        var response = await Client.GetAsync("/api/v1/telemetry/timeline?limit=5");
        Client.DefaultRequestHeaders.Remove("Authorization");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"events\":[]");
    }

    // ──────── SESSION ANALYTICS ────────

    [Fact]
    public async Task GET_Sessions_FromGreaterThanTo_Returns400()
    {
        var jwt = GetJwt("p1", "t1");

        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        var response = await Client.GetAsync("/api/v1/analytics/sessions?from=2030-01-01T00:00:00Z&to=2020-01-01T00:00:00Z");
        Client.DefaultRequestHeaders.Remove("Authorization");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GET_Sessions_WithoutJwt_Returns401()
    {
        var response = await Client.GetAsync("/api/v1/analytics/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ──────── JWT CLAIM VALIDATION ────────

    [Fact]
    public async Task GET_Timeline_WithLongClaimValues_Returns400()
    {
        var longId = new string('x', 300);
        var jwt = GetJwt(longId, "valid");

        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        var response = await Client.GetAsync("/api/v1/telemetry/timeline?limit=1");
        Client.DefaultRequestHeaders.Remove("Authorization");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

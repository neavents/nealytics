using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class FeatureQueryIntegrationTests : IntegrationTestBase, IAsyncLifetime
{
    public FeatureQueryIntegrationTests(TestWebApplicationFactory factory) : base(factory) { }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task Ingest(string projectId, string tenantId, string sessionId, string eventType)
    {
        var payload = new
        {
            projectId,
            tenantId,
            sessionId,
            eventType,
            timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            metadataJson = "{}"
        };
        HttpResponseMessage response = await Client.PostAsJsonAsync("/api/v1/telemetry/track?k=test-key-1", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Timeline_EventTypeFilter_ReturnsOnlyMatchingEvents()
    {
        string tenantId = $"t-filter-{Guid.NewGuid():N}";
        string projectId = "p-filter";

        await Ingest(projectId, tenantId, "s1", "click");
        await Ingest(projectId, tenantId, "s1", "click");
        await Ingest(projectId, tenantId, "s1", "scroll");

        await Task.Delay(4000);

        string jwt = GetJwt(projectId, tenantId);
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        HttpResponseMessage response = await Client.GetAsync("/api/v1/telemetry/timeline?limit=50&eventType=click");
        Client.DefaultRequestHeaders.Remove("Authorization");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement events = body.GetProperty("events");
        events.GetArrayLength().Should().Be(2, "only the two 'click' events match the filter");
        foreach (JsonElement evt in events.EnumerateArray())
        {
            evt.GetProperty("eventType").GetString().Should().Be("click");
        }
    }

    [Fact]
    public async Task Timeline_SessionFilter_ReturnsOnlyMatchingSession()
    {
        string tenantId = $"t-sess-{Guid.NewGuid():N}";
        string projectId = "p-filter";

        await Ingest(projectId, tenantId, "sess-A", "view");
        await Ingest(projectId, tenantId, "sess-B", "view");

        await Task.Delay(4000);

        string jwt = GetJwt(projectId, tenantId);
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        HttpResponseMessage response = await Client.GetAsync("/api/v1/telemetry/timeline?limit=50&sessionId=sess-A");
        Client.DefaultRequestHeaders.Remove("Authorization");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement events = body.GetProperty("events");
        events.GetArrayLength().Should().Be(1);
        events[0].GetProperty("sessionId").GetString().Should().Be("sess-A");
    }

    [Fact]
    public async Task TimeSeries_BucketsEvents_AndReportsTotalCount()
    {
        string tenantId = $"t-ts-{Guid.NewGuid():N}";
        string projectId = "p-ts";

        await Ingest(projectId, tenantId, "s1", "hit");
        await Ingest(projectId, tenantId, "s1", "hit");
        await Ingest(projectId, tenantId, "s2", "hit");

        await Task.Delay(4000);

        string jwt = GetJwt(projectId, tenantId);
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        HttpResponseMessage response = await Client.GetAsync(
            "/api/v1/analytics/timeseries?from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z&interval=day");
        Client.DefaultRequestHeaders.Remove("Authorization");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("interval").GetString().Should().Be("day");
        body.GetProperty("totalCount").GetInt64().Should().Be(3);

        JsonElement points = body.GetProperty("points");
        points.GetArrayLength().Should().Be(1, "all three events fall in the same day bucket");
        points[0].GetProperty("count").GetInt64().Should().Be(3);
    }

    [Fact]
    public async Task TimeSeries_EventTypeFilter_CountsOnlyMatchingEvents()
    {
        string tenantId = $"t-tsf-{Guid.NewGuid():N}";
        string projectId = "p-ts";

        await Ingest(projectId, tenantId, "s1", "signup");
        await Ingest(projectId, tenantId, "s1", "login");
        await Ingest(projectId, tenantId, "s1", "login");

        await Task.Delay(4000);

        string jwt = GetJwt(projectId, tenantId);
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        HttpResponseMessage response = await Client.GetAsync(
            "/api/v1/analytics/timeseries?from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z&interval=hour&eventType=login");
        Client.DefaultRequestHeaders.Remove("Authorization");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalCount").GetInt64().Should().Be(2, "only 'login' events should be counted");
    }

    [Fact]
    public async Task TimeSeries_InvalidInterval_Returns400()
    {
        string jwt = GetJwt("p-ts", "t-ts");
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        HttpResponseMessage response = await Client.GetAsync("/api/v1/analytics/timeseries?interval=week");
        Client.DefaultRequestHeaders.Remove("Authorization");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TimeSeries_FromAfterTo_Returns400()
    {
        string jwt = GetJwt("p-ts", "t-ts");
        Client.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        HttpResponseMessage response = await Client.GetAsync(
            "/api/v1/analytics/timeseries?from=2030-01-01T00:00:00Z&to=2020-01-01T00:00:00Z");
        Client.DefaultRequestHeaders.Remove("Authorization");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TimeSeries_WithoutJwt_Returns401()
    {
        HttpResponseMessage response = await Client.GetAsync("/api/v1/analytics/timeseries");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

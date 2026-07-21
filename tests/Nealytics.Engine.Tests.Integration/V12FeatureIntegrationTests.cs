using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class V12FeatureIntegrationTests : IntegrationTestBase, IAsyncLifetime
{
    public V12FeatureIntegrationTests(TestWebApplicationFactory factory) : base(factory) { }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task Ingest(
        string projectId, string tenantId, string sessionId, string eventType,
        string? userId = null, string? itemId = null, string? metadataJson = null, DateTime? timestamp = null)
    {
        var payload = new
        {
            projectId,
            tenantId,
            sessionId,
            userId,
            eventType,
            itemId,
            timestamp = (timestamp ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            metadataJson = metadataJson ?? "{}"
        };
        HttpResponseMessage response = await Client.PostAsJsonAsync("/api/v1/telemetry/track?k=test-key-1", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private async Task<JsonElement> Get(string projectId, string tenantId, string url)
    {
        string jwt = GetJwt(projectId, tenantId);
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {jwt}");
        HttpResponseMessage response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpResponseMessage> GetRaw(string projectId, string tenantId, string url)
    {
        string jwt = GetJwt(projectId, tenantId);
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {jwt}");
        return await Client.SendAsync(request);
    }

    // ──────── 4.1 Active Users / DAU / MAU ────────

    [Fact]
    public async Task ActiveUsers_ByUser_CountsDistinctUsersPerDay_AndSkipsAnonymous()
    {
        string tenantId = $"t-dau-{Guid.NewGuid():N}";
        string projectId = "p-dau";
        DateTime today = DateTime.UtcNow.AddHours(-1);
        DateTime yesterday = today.AddDays(-1);

        await Ingest(projectId, tenantId, "s1", "hit", userId: "u1", timestamp: today);
        await Ingest(projectId, tenantId, "s2", "hit", userId: "u2", timestamp: today);
        await Ingest(projectId, tenantId, "s3", "hit", userId: "u2", timestamp: today);
        await Ingest(projectId, tenantId, "s4", "hit", userId: null, timestamp: today);
        await Ingest(projectId, tenantId, "s5", "hit", userId: "u9", timestamp: yesterday);

        await Task.Delay(4000);
        await ClickHouseTestSupport.OptimizeFinalAsync();

        string from = yesterday.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        string to = today.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        JsonElement body = await Get(projectId, tenantId,
            $"/api/v1/analytics/active?interval=day&by=user&mode=exact&from={from}&to={to}");

        body.GetProperty("by").GetString().Should().Be("user");
        body.GetProperty("interval").GetString().Should().Be("day");
        JsonElement points = body.GetProperty("points");
        points.GetArrayLength().Should().Be(2, "events span two distinct days");

        long todayCount = points[1].GetProperty("activeCount").GetInt64();
        long yesterdayCount = points[0].GetProperty("activeCount").GetInt64();
        todayCount.Should().Be(2, "u1 and u2 are the only distinct non-null users today (anonymous skipped)");
        yesterdayCount.Should().Be(1);
    }

    [Fact]
    public async Task ActiveUsers_BySession_CountsDistinctSessions()
    {
        string tenantId = $"t-dau-sess-{Guid.NewGuid():N}";
        string projectId = "p-dau";
        DateTime now = DateTime.UtcNow.AddHours(-1);

        await Ingest(projectId, tenantId, "sess-a", "hit", timestamp: now);
        await Ingest(projectId, tenantId, "sess-a", "hit", timestamp: now);
        await Ingest(projectId, tenantId, "sess-b", "hit", timestamp: now);

        await Task.Delay(4000);

        string from = now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        string to = now.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        JsonElement body = await Get(projectId, tenantId,
            $"/api/v1/analytics/active?interval=day&by=session&from={from}&to={to}");

        body.GetProperty("by").GetString().Should().Be("session");
        JsonElement points = body.GetProperty("points");
        long total = 0;
        foreach (JsonElement p in points.EnumerateArray())
        {
            total += p.GetProperty("activeCount").GetInt64();
        }
        total.Should().Be(2, "sess-a and sess-b are the two distinct sessions");
    }

    [Fact]
    public async Task ActiveUsers_IsTenantIsolated()
    {
        string projectId = "p-iso";
        string tenantA = $"t-A-{Guid.NewGuid():N}";
        string tenantB = $"t-B-{Guid.NewGuid():N}";
        DateTime now = DateTime.UtcNow.AddHours(-1);

        await Ingest(projectId, tenantA, "s1", "hit", userId: "ua1", timestamp: now);
        await Ingest(projectId, tenantA, "s2", "hit", userId: "ua2", timestamp: now);
        await Ingest(projectId, tenantB, "s3", "hit", userId: "ub1", timestamp: now);

        await Task.Delay(4000);

        string from = now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        string to = now.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        JsonElement body = await Get(projectId, tenantA,
            $"/api/v1/analytics/active?interval=day&by=user&from={from}&to={to}");

        long total = 0;
        foreach (JsonElement p in body.GetProperty("points").EnumerateArray())
        {
            total += p.GetProperty("activeCount").GetInt64();
        }
        total.Should().Be(2, "tenant A must not see tenant B's users");
    }

    [Theory]
    [InlineData("/api/v1/analytics/active?interval=week")]
    [InlineData("/api/v1/analytics/active?by=device")]
    [InlineData("/api/v1/analytics/active?mode=estimate")]
    [InlineData("/api/v1/analytics/active?from=2030-01-01T00:00:00Z&to=2020-01-01T00:00:00Z")]
    public async Task ActiveUsers_InvalidInputs_Return400(string url)
    {
        HttpResponseMessage response = await GetRaw("p", "t", url);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ActiveUsers_WithoutJwt_Returns401()
    {
        HttpResponseMessage response = await Client.GetAsync("/api/v1/analytics/active");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ──────── 4.2 groupBy time-series ────────

    [Fact]
    public async Task TimeSeries_GroupByEventType_ReturnsPerSeriesCounts()
    {
        string tenantId = $"t-grp-{Guid.NewGuid():N}";
        string projectId = "p-grp";

        await Ingest(projectId, tenantId, "s1", "click");
        await Ingest(projectId, tenantId, "s1", "click");
        await Ingest(projectId, tenantId, "s1", "scroll");

        await Task.Delay(4000);

        JsonElement body = await Get(projectId, tenantId,
            "/api/v1/analytics/timeseries?from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z&interval=day&groupBy=event_type");

        JsonElement points = body.GetProperty("points");
        Dictionary<string, long> bySeries = new Dictionary<string, long>();
        foreach (JsonElement p in points.EnumerateArray())
        {
            string series = p.GetProperty("series").GetString()!;
            bySeries[series] = bySeries.GetValueOrDefault(series) + p.GetProperty("count").GetInt64();
        }

        bySeries["click"].Should().Be(2);
        bySeries["scroll"].Should().Be(1);
        body.GetProperty("totalCount").GetInt64().Should().Be(3);
    }

    [Fact]
    public async Task TimeSeries_WithoutGroupBy_HasNullSeries()
    {
        string tenantId = $"t-nogrp-{Guid.NewGuid():N}";
        string projectId = "p-grp";

        await Ingest(projectId, tenantId, "s1", "ping");
        await Task.Delay(4000);

        JsonElement body = await Get(projectId, tenantId,
            "/api/v1/analytics/timeseries?from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z&interval=day");

        JsonElement first = body.GetProperty("points")[0];
        (first.TryGetProperty("series", out JsonElement series) == false || series.ValueKind == JsonValueKind.Null)
            .Should().BeTrue("ungrouped points carry no series");
    }

    [Fact]
    public async Task TimeSeries_InvalidGroupBy_Returns400()
    {
        HttpResponseMessage response = await GetRaw("p", "t",
            "/api/v1/analytics/timeseries?groupBy=user_id");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ──────── 4.3 Top-N ────────

    [Fact]
    public async Task TopEvents_ByEventType_OrdersByCountDescending()
    {
        string tenantId = $"t-top-{Guid.NewGuid():N}";
        string projectId = "p-top";

        await Ingest(projectId, tenantId, "s1", "view");
        await Ingest(projectId, tenantId, "s1", "view");
        await Ingest(projectId, tenantId, "s1", "view");
        await Ingest(projectId, tenantId, "s1", "click");
        await Ingest(projectId, tenantId, "s1", "purchase");
        await Ingest(projectId, tenantId, "s1", "purchase");

        await Task.Delay(4000);

        JsonElement body = await Get(projectId, tenantId,
            "/api/v1/analytics/top?dimension=event_type&from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z&limit=10");

        JsonElement items = body.GetProperty("items");
        items.GetArrayLength().Should().Be(3);
        items[0].GetProperty("key").GetString().Should().Be("view");
        items[0].GetProperty("count").GetInt64().Should().Be(3);
        items[1].GetProperty("count").GetInt64().Should().Be(2);
        items[2].GetProperty("count").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task TopEvents_ByItemId_ExcludesNullKeys()
    {
        string tenantId = $"t-topitem-{Guid.NewGuid():N}";
        string projectId = "p-top";

        await Ingest(projectId, tenantId, "s1", "view", itemId: "/home");
        await Ingest(projectId, tenantId, "s1", "view", itemId: "/home");
        await Ingest(projectId, tenantId, "s1", "view", itemId: null);

        await Task.Delay(4000);

        JsonElement body = await Get(projectId, tenantId,
            "/api/v1/analytics/top?dimension=item_id&from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z");

        JsonElement items = body.GetProperty("items");
        items.GetArrayLength().Should().Be(1, "the NULL item_id row must be excluded");
        items[0].GetProperty("key").GetString().Should().Be("/home");
        items[0].GetProperty("count").GetInt64().Should().Be(2);
    }

    [Fact]
    public async Task TopEvents_IsTenantIsolated()
    {
        string projectId = "p-topiso";
        string tenantA = $"t-A-{Guid.NewGuid():N}";
        string tenantB = $"t-B-{Guid.NewGuid():N}";

        await Ingest(projectId, tenantA, "s1", "only-A");
        await Ingest(projectId, tenantB, "s2", "only-B");

        await Task.Delay(4000);

        JsonElement body = await Get(projectId, tenantA,
            "/api/v1/analytics/top?from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z");

        foreach (JsonElement item in body.GetProperty("items").EnumerateArray())
        {
            item.GetProperty("key").GetString().Should().NotBe("only-B");
        }
    }

    [Fact]
    public async Task TopEvents_InvalidDimension_Returns400()
    {
        HttpResponseMessage response = await GetRaw("p", "t",
            "/api/v1/analytics/top?dimension=session_id");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ──────── Section 3 user_id on ingest + timeline ────────

    [Fact]
    public async Task Track_WithUserId_PersistsAndSurfacesOnTimeline()
    {
        string tenantId = $"t-uid-{Guid.NewGuid():N}";
        string projectId = "p-uid";

        await Ingest(projectId, tenantId, "s1", "identified", userId: "user-42");
        await Ingest(projectId, tenantId, "s2", "anonymous", userId: null);

        await Task.Delay(4000);

        JsonElement body = await Get(projectId, tenantId, "/api/v1/telemetry/timeline?limit=50");
        JsonElement events = body.GetProperty("events");
        events.GetArrayLength().Should().Be(2);

        Dictionary<string, string?> byType = new Dictionary<string, string?>();
        foreach (JsonElement evt in events.EnumerateArray())
        {
            string type = evt.GetProperty("eventType").GetString()!;
            byType[type] = evt.TryGetProperty("userId", out JsonElement uid) && uid.ValueKind != JsonValueKind.Null
                ? uid.GetString()
                : null;
        }

        byType["identified"].Should().Be("user-42");
        byType["anonymous"].Should().BeNull();
    }

    // ──────── 4.4 metadata JSON filtering ────────

    [Fact]
    public async Task Timeline_MetadataFilter_ReturnsOnlyMatchingEvents()
    {
        string tenantId = $"t-meta-{Guid.NewGuid():N}";
        string projectId = "p-meta";

        await Ingest(projectId, tenantId, "s1", "purchase", metadataJson: "{\"plan\":\"pro\"}");
        await Ingest(projectId, tenantId, "s1", "purchase", metadataJson: "{\"plan\":\"free\"}");
        await Ingest(projectId, tenantId, "s1", "purchase", metadataJson: "{\"plan\":\"pro\"}");

        await Task.Delay(4000);

        JsonElement body = await Get(projectId, tenantId,
            "/api/v1/telemetry/timeline?limit=50&metaKey=plan&metaValue=pro");

        JsonElement events = body.GetProperty("events");
        events.GetArrayLength().Should().Be(2, "only the two pro-plan events match");
    }

    // ──────── 4.5 gzip request decompression ────────

    [Fact]
    public async Task Track_WithGzipEncodedBody_IsDecompressedAndAccepted()
    {
        string tenantId = $"t-gzip-{Guid.NewGuid():N}";
        string projectId = "p-gzip";

        string json = JsonSerializer.Serialize(new
        {
            projectId,
            tenantId,
            sessionId = "s-gzip",
            eventType = "compressed",
            timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            metadataJson = "{}"
        });

        using MemoryStream compressed = new MemoryStream();
        using (GZipStream gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] raw = Encoding.UTF8.GetBytes(json);
            gzip.Write(raw, 0, raw.Length);
        }
        compressed.Position = 0;

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telemetry/track?k=test-key-1");
        ByteArrayContent content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        request.Content = content;

        HttpResponseMessage response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await Task.Delay(4000);

        JsonElement body = await Get(projectId, tenantId, "/api/v1/telemetry/timeline?limit=50");
        body.GetProperty("events").GetArrayLength().Should().Be(1, "the gzipped event must be decompressed and stored");
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class PivotAndDistributionIntegrationTests : IntegrationTestBase, IAsyncLifetime
{
    private const string Project = "p-pivot";

    public PivotAndDistributionIntegrationTests(TestWebApplicationFactory factory) : base(factory) { }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ClickHouseTestSupport.DeleteProjectsAsync(Project);

    private async Task Ingest(string tenantId, string sessionId, string eventType, string? objectId, DateTime at, string traffic = "normal")
    {
        var payload = new
        {
            projectId = Project,
            tenantId,
            sessionId,
            eventType,
            objectId,
            trafficClass = traffic,
            timestamp = at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            metadataJson = "{}",
        };

        HttpResponseMessage response = await Client.PostAsJsonAsync("/api/v1/telemetry/track?k=test-key-1", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private async Task<HttpResponseMessage> GetRaw(string tenantId, string url)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {GetJwt(Project, tenantId)}");
        return await Client.SendAsync(request);
    }

    private async Task<JsonElement> Get(string tenantId, string url)
    {
        HttpResponseMessage response = await GetRaw(tenantId, url);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private const string Range = "&from=2020-01-01T00:00:00Z&to=2099-01-01T00:00:00Z";

    private static async Task SettleAsync() => await Task.Delay(2500);

    private async Task<string> SeedAsync()
    {
        string tenant = $"t-{Guid.NewGuid():N}";
        DateTime t0 = DateTime.UtcNow.Date.AddDays(-3).AddHours(12);

        await Ingest(tenant, "s1", "impression", "a", t0);
        await Ingest(tenant, "s1", "impression", "b", t0.AddSeconds(1));
        await Ingest(tenant, "s1", "open", "a", t0.AddSeconds(30));
        await Ingest(tenant, "s2", "impression", "a", t0.AddMinutes(5));
        await Ingest(tenant, "s2", "impression", "b", t0.AddMinutes(5).AddSeconds(2));
        await Ingest(tenant, "s3", "impression", "a", t0.AddMinutes(10));
        await Ingest(tenant, "s3", "open", "a", t0.AddMinutes(10).AddSeconds(90));
        await Ingest(tenant, "s3", "open", "b", t0.AddMinutes(12));
        await Ingest(tenant, "crawler", "impression", "a", t0, "bot");
        await SettleAsync();
        return tenant;
    }

    [Fact]
    public async Task Pivot_AnswersSeveralScopedMetricsPerKeyInOneRead()
    {
        string tenant = await SeedAsync();

        JsonElement body = await Get(tenant,
            "/api/v1/analytics/pivot?groupBy=object_id&metric=sessions:impression&metric=sessions:open&metric=events&orderBy=1" + Range);

        body.GetProperty("source").GetString().Should().Be("raw");
        body.GetProperty("truncated").GetBoolean().Should().BeFalse();
        body.GetProperty("groupCount").GetInt64().Should().Be(2);

        JsonElement rows = body.GetProperty("rows");
        rows.GetArrayLength().Should().Be(2);
        rows[0].GetProperty("key").GetString().Should().Be("a");
        rows[0].GetProperty("values").EnumerateArray().Select(v => v.GetDouble()).Should().Equal(3, 2, 5);
        rows[1].GetProperty("key").GetString().Should().Be("b");
        rows[1].GetProperty("values").EnumerateArray().Select(v => v.GetDouble()).Should().Equal(2, 1, 3);

        body.GetProperty("totals").EnumerateArray().Select(v => v.GetDouble()).Should().Equal(3, 2, 8);
        body.GetProperty("metrics")[0].GetProperty("grain").GetString().Should().Be("session");
        body.GetProperty("metrics")[2].GetProperty("grain").GetString().Should().Be("event");
    }

    [Fact]
    public async Task Pivot_DistinctCountsAColumn_AndTrafficAllBringsTheBotBack()
    {
        string tenant = await SeedAsync();

        JsonElement normal = await Get(tenant, "/api/v1/analytics/pivot?groupBy=event_type&metric=distinct(session_id)&orderBy=key" + Range);
        JsonElement all = await Get(tenant, "/api/v1/analytics/pivot?groupBy=event_type&metric=distinct(session_id)&orderBy=key&traffic=all" + Range);

        normal.GetProperty("rows")[0].GetProperty("values")[0].GetDouble().Should().Be(3);
        all.GetProperty("rows")[0].GetProperty("values")[0].GetDouble().Should().Be(4);
    }

    [Fact]
    public async Task Pivot_RefusesAnUnknownMetricAndAnOrderOutOfRange()
    {
        string tenant = $"t-{Guid.NewGuid():N}";

        (await GetRaw(tenant, "/api/v1/analytics/pivot?groupBy=object_id&metric=median(x)")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetRaw(tenant, "/api/v1/analytics/pivot?groupBy=object_id&metric=events&orderBy=4")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetRaw(tenant, "/api/v1/analytics/pivot?groupBy=object_id")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetRaw(tenant, "/api/v1/analytics/pivot?groupBy=nope&metric=events")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Distribution_OfSessionDuration_ReportsQuantilesAndBuckets()
    {
        string tenant = await SeedAsync();

        JsonElement body = await Get(tenant,
            "/api/v1/analytics/distribution?of=session_duration&quantiles=0.5,1&buckets=1000,60000" + Range);

        body.GetProperty("unit").GetString().Should().Be("ms");
        body.GetProperty("count").GetInt64().Should().Be(3);
        body.GetProperty("min").GetDouble().Should().Be(2000);
        body.GetProperty("max").GetDouble().Should().Be(120000);

        JsonElement quantiles = body.GetProperty("quantiles");
        quantiles[0].GetProperty("q").GetDouble().Should().Be(0.5);
        quantiles[0].GetProperty("value").GetDouble().Should().Be(30000);
        quantiles[1].GetProperty("value").GetDouble().Should().Be(120000);

        JsonElement buckets = body.GetProperty("buckets");
        buckets.GetArrayLength().Should().Be(3);
        buckets[0].GetProperty("count").GetInt64().Should().Be(0);
        buckets[1].GetProperty("count").GetInt64().Should().Be(2);
        buckets[2].GetProperty("count").GetInt64().Should().Be(1);
        buckets[2].GetProperty("from").GetDouble().Should().Be(60000);
        buckets[2].GetProperty("to").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Distribution_OfSessionEvents_AndOfAnEmptyRange()
    {
        string tenant = await SeedAsync();

        JsonElement events = await Get(tenant, "/api/v1/analytics/distribution?of=session_events&quantiles=0.5" + Range);
        events.GetProperty("count").GetInt64().Should().Be(3);
        events.GetProperty("avg").GetDouble().Should().BeApproximately(8d / 3, 0.001);

        JsonElement empty = await Get(tenant,
            "/api/v1/analytics/distribution?of=session_events&from=2001-01-01T00:00:00Z&to=2001-01-02T00:00:00Z");
        empty.GetProperty("count").GetInt64().Should().Be(0);
        empty.GetProperty("quantiles")[0].GetProperty("value").GetDouble().Should().Be(0);
    }

    [Fact]
    public async Task Distribution_RefusesBadInput()
    {
        string tenant = $"t-{Guid.NewGuid():N}";

        (await GetRaw(tenant, "/api/v1/analytics/distribution?of=nothing")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetRaw(tenant, "/api/v1/analytics/distribution?of=session_duration&quantiles=2")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetRaw(tenant, "/api/v1/analytics/distribution?of=session_duration&buckets=10,5")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetRaw(tenant, "/api/v1/analytics/distribution?of=session_duration&eventType=open")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sessions_ExcludeBotsByDefault_AndFilterByColumn()
    {
        string tenant = await SeedAsync();

        JsonElement normal = await Get(tenant, "/api/v1/analytics/sessions?limit=10" + Range);
        JsonElement all = await Get(tenant, "/api/v1/analytics/sessions?limit=10&traffic=all" + Range);
        JsonElement scoped = await Get(tenant, "/api/v1/analytics/sessions?limit=10&filter=object_id:b" + Range);

        normal.GetProperty("uniqueSessionCount").GetInt64().Should().Be(3);
        all.GetProperty("uniqueSessionCount").GetInt64().Should().Be(4);
        scoped.GetProperty("uniqueSessionCount").GetInt64().Should().Be(3);
        scoped.GetProperty("totalEventCount").GetInt64().Should().Be(3);
    }
}

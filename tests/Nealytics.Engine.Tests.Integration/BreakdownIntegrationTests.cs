using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Integration;

/// <summary>
/// The breakdown endpoint against a real ClickHouse.
///
/// Grouping is exercised on core columns rather than on a declared dimension, deliberately: this
/// factory shares <c>global_events</c> with the running deployment, and declaring a test dimension
/// here would have the reconciler add a column to that table which the deployment does not declare
/// — and the moment a test wrote a value into it, the real service would refuse to boot. The
/// allowlist's handling of declared dimensions is covered in <c>BreakdownTests</c>, which needs no
/// database.
/// </summary>
[Collection("ClickHouse")]
public class BreakdownIntegrationTests : IntegrationTestBase, IAsyncLifetime
{
    public BreakdownIntegrationTests(TestWebApplicationFactory factory) : base(factory) { }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Every test in this class writes under the fixed project <c>p-bd</c>, isolating itself with a
    /// per-test tenant instead. That keeps the assertions independent but leaves the rows behind,
    /// and the table is shared with the running estate — so the class removes its own project when
    /// it is done. Measured before this: 104 rows, growing by a full set every run.
    /// </summary>
    public Task DisposeAsync() => ClickHouseTestSupport.DeleteProjectsAsync("p-bd");

    private async Task Ingest(
        string projectId, string tenantId, string sessionId, string eventType,
        string? userId = null, string? objectId = null)
    {
        var payload = new
        {
            projectId,
            tenantId,
            sessionId,
            userId,
            eventType,
            objectId,
            timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            metadataJson = "{}",
        };

        HttpResponseMessage response =
            await Client.PostAsJsonAsync("/api/v1/telemetry/track?k=test-key-1", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private async Task<HttpResponseMessage> GetRaw(string projectId, string tenantId, string url)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {GetJwt(projectId, tenantId)}");
        return await Client.SendAsync(request);
    }

    private async Task<JsonElement> Get(string projectId, string tenantId, string url)
    {
        HttpResponseMessage response = await GetRaw(projectId, tenantId, url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Range() =>
        "&" + ClickHouseTestSupport.RecentRange;

    private static async Task SettleAsync() => await Task.Delay(2500);

    [Fact]
    public async Task Breakdown_ByEventType_RanksCorrectly_AndSharesSumToOne()
    {
        string tenantId = $"t-bd-{Guid.NewGuid():N}";
        const string projectId = "p-bd";

        await Ingest(projectId, tenantId, "s1", "menu_view");
        await Ingest(projectId, tenantId, "s2", "menu_view");
        await Ingest(projectId, tenantId, "s3", "menu_view");
        await Ingest(projectId, tenantId, "s4", "item_view");
        await SettleAsync();

        JsonElement body = await Get(projectId, tenantId,
            $"/api/v1/analytics/breakdown?metric=events&groupBy=event_type{Range()}");

        JsonElement rows = body.GetProperty("rows");
        rows.GetArrayLength().Should().Be(2);

        rows[0].GetProperty("key").GetString().Should().Be("menu_view");
        rows[0].GetProperty("value").GetInt64().Should().Be(3);
        rows[1].GetProperty("key").GetString().Should().Be("item_view");
        rows[1].GetProperty("value").GetInt64().Should().Be(1);

        body.GetProperty("total").GetInt64().Should().Be(4);
        body.GetProperty("truncated").GetBoolean().Should().BeFalse();

        rows[0].GetProperty("share").GetDouble().Should().BeApproximately(0.75, 0.0001);
        rows[1].GetProperty("share").GetDouble().Should().BeApproximately(0.25, 0.0001);
    }

    [Fact]
    public async Task Breakdown_BySessions_CountsVisitsNotTaps()
    {
        string tenantId = $"t-bd-{Guid.NewGuid():N}";
        const string projectId = "p-bd";

        // One session, three events. A per-session metric must say 1, not 3 — that difference is
        // the whole point of offering the metric.
        await Ingest(projectId, tenantId, "same-session", "menu_view");
        await Ingest(projectId, tenantId, "same-session", "menu_view");
        await Ingest(projectId, tenantId, "same-session", "menu_view");
        await Ingest(projectId, tenantId, "other-session", "menu_view");
        await SettleAsync();

        JsonElement body = await Get(projectId, tenantId,
            $"/api/v1/analytics/breakdown?metric=sessions&eventType=menu_view&groupBy=event_type{Range()}");

        body.GetProperty("rows")[0].GetProperty("value").GetInt64().Should().Be(2);
    }

    [Fact]
    public async Task Breakdown_ByUsers_SkipsAnonymousRows()
    {
        string tenantId = $"t-bd-{Guid.NewGuid():N}";
        const string projectId = "p-bd";

        await Ingest(projectId, tenantId, "s1", "hit", userId: "u1");
        await Ingest(projectId, tenantId, "s2", "hit", userId: "u1");
        await Ingest(projectId, tenantId, "s3", "hit", userId: "u2");
        await Ingest(projectId, tenantId, "s4", "hit", userId: null);
        await Ingest(projectId, tenantId, "s5", "hit", userId: null);
        await SettleAsync();

        JsonElement body = await Get(projectId, tenantId,
            $"/api/v1/analytics/breakdown?metric=users&groupBy=event_type{Range()}");

        body.GetProperty("rows")[0].GetProperty("value").GetInt64().Should()
            .Be(2, "two known users; the anonymous rows must not become a third");
    }

    [Fact]
    public async Task Breakdown_ReportsTruncation_RatherThanQuietlyCapping()
    {
        string tenantId = $"t-bd-{Guid.NewGuid():N}";
        const string projectId = "p-bd";

        for (int i = 0; i < 5; i++)
        {
            await Ingest(projectId, tenantId, $"s{i}", "hit", objectId: $"object-{i}");
        }

        await SettleAsync();

        JsonElement body = await Get(projectId, tenantId,
            $"/api/v1/analytics/breakdown?metric=events&groupBy=object_id&limit=2{Range()}");

        body.GetProperty("rows").GetArrayLength().Should().Be(2);
        body.GetProperty("groupCount").GetInt64().Should().Be(5);
        body.GetProperty("truncated").GetBoolean().Should()
            .BeTrue("a cap the caller cannot see is a chart that is simply wrong");

        // Shares are against the grand total, so a capped response adds up to less than one —
        // which is the honest reading of a partial list.
        double returnedShare = body.GetProperty("rows").EnumerateArray()
            .Sum(row => row.GetProperty("share").GetDouble());
        returnedShare.Should().BeLessThan(1.0);
        body.GetProperty("total").GetInt64().Should().Be(5);
    }

    [Fact]
    public async Task Breakdown_NotTruncated_WhenEveryGroupFits()
    {
        string tenantId = $"t-bd-{Guid.NewGuid():N}";
        const string projectId = "p-bd";

        await Ingest(projectId, tenantId, "s1", "hit", objectId: "a");
        await Ingest(projectId, tenantId, "s2", "hit", objectId: "b");
        await SettleAsync();

        JsonElement body = await Get(projectId, tenantId,
            $"/api/v1/analytics/breakdown?metric=events&groupBy=object_id&limit=50{Range()}");

        body.GetProperty("truncated").GetBoolean().Should().BeFalse();
        body.GetProperty("groupCount").GetInt64().Should().Be(2);
    }

    [Fact]
    public async Task Breakdown_UnknownGroupBy_Returns400_NamingIt()
    {
        HttpResponseMessage response = await GetRaw("p-bd", "t-bd",
            $"/api/v1/analytics/breakdown?metric=events&groupBy=not_a_column{Range()}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not_a_column");
    }

    [Fact]
    public async Task Breakdown_InjectionAttempt_Returns400_AndTheTableSurvives()
    {
        // The single most security-relevant path in the endpoint. The assertion is not only the
        // 400 — it is that global_events is still queryable afterwards.
        HttpResponseMessage response = await GetRaw("p-bd", "t-bd",
            "/api/v1/analytics/breakdown?metric=events&groupBy="
            + Uri.EscapeDataString("event_type; DROP TABLE nealytics_core.global_events") + Range());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        HttpResponseMessage after = await GetRaw("p-bd", "t-bd",
            $"/api/v1/analytics/breakdown?metric=events&groupBy=event_type{Range()}");
        after.StatusCode.Should().Be(HttpStatusCode.OK,
            "the table must still be there — this is the whole point");
    }

    [Fact]
    public async Task Breakdown_UnknownFilterName_Returns400()
    {
        HttpResponseMessage response = await GetRaw("p-bd", "t-bd",
            $"/api/v1/analytics/breakdown?metric=events&groupBy=event_type&filter=made_up:x{Range()}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("made_up");
    }

    [Fact]
    public async Task Breakdown_FilterNarrowsTheResult()
    {
        string tenantId = $"t-bd-{Guid.NewGuid():N}";
        const string projectId = "p-bd";

        await Ingest(projectId, tenantId, "s1", "hit", objectId: "keep");
        await Ingest(projectId, tenantId, "s2", "hit", objectId: "drop");
        await SettleAsync();

        JsonElement body = await Get(projectId, tenantId,
            $"/api/v1/analytics/breakdown?metric=events&groupBy=object_id&filter=object_id:keep{Range()}");

        body.GetProperty("rows").GetArrayLength().Should().Be(1);
        body.GetProperty("rows")[0].GetProperty("key").GetString().Should().Be("keep");
    }

    [Fact]
    public async Task Breakdown_NeverCrossesTenants()
    {
        string mine = $"t-bd-{Guid.NewGuid():N}";
        string theirs = $"t-bd-{Guid.NewGuid():N}";
        const string projectId = "p-bd";

        await Ingest(projectId, mine, "s1", "hit", objectId: "mine");
        await Ingest(projectId, theirs, "s2", "hit", objectId: "theirs");
        await SettleAsync();

        JsonElement body = await Get(projectId, mine,
            $"/api/v1/analytics/breakdown?metric=events&groupBy=object_id{Range()}");

        body.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("key").GetString())
            .Should().NotContain("theirs");
    }

    [Fact]
    public async Task Breakdown_WithoutAJwt_Is401()
    {
        HttpResponseMessage response =
            await Client.GetAsync($"/api/v1/analytics/breakdown?metric=events&groupBy=event_type{Range()}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Breakdown_NullDimensionValues_BecomeAnEmptyKeyRatherThanVanishing()
    {
        // Rows with no value for the grouped column must still be counted, in a bucket that says
        // "not recorded" — dropping them makes a total that silently disagrees with the event count.
        string tenantId = $"t-bd-{Guid.NewGuid():N}";
        const string projectId = "p-bd";

        await Ingest(projectId, tenantId, "s1", "hit", objectId: "present");
        await Ingest(projectId, tenantId, "s2", "hit", objectId: null);
        await SettleAsync();

        JsonElement body = await Get(projectId, tenantId,
            $"/api/v1/analytics/breakdown?metric=events&groupBy=object_id{Range()}");

        body.GetProperty("total").GetInt64().Should().Be(2);
        body.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("key").GetString())
            .Should().Contain("");
    }
}

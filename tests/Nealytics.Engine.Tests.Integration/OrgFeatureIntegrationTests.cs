using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetFunnel;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Features.GetProjectTimeline;
using Nealytics.Engine.Features.GetRetention;
using Nealytics.Engine.Features.UpsertTenantAttributes;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class OrgFeatureIntegrationTests : IAsyncLifetime
{
    private const string Table = "nealytics_core.global_events";
    private const string Currency = "orgp_currency";
    private const string Amount = "orgp_amount";

    private readonly string _project = $"p-org-{Guid.NewGuid():N}"[..20];
    private readonly string _rollup = $"orgp_{Guid.NewGuid():N}"[..16];
    private readonly string _org = $"org-{Guid.NewGuid():N}"[..14];

    private static readonly DateTime Day = DateTime.UtcNow.Date.AddDays(-3);
    private static readonly DateTime From = Day.AddDays(-1);
    private static readonly DateTime To = Day.AddDays(2);

    private TelemetryEngineOptions _options = new();
    private ClickHouseConnectionFactory _connections = null!;
    private RollupRegistry _rollups = null!;
    private TenantAttributeRegistry _attributes = null!;

    public async Task InitializeAsync()
    {
        await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {Currency} LowCardinality(String) DEFAULT ''");
        await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {Amount} Nullable(Decimal(18, 4))");

        _options = new TelemetryEngineOptions
        {
            ClickHouseConnectionString = ClickHouseTestSupport.ConnectionString,
            ConnectionPoolSize = 4,
            Dimensions = [new DimensionOptions { Name = Currency, Type = "LowCardinality" }],
            Measures = [new MeasureOptions { Name = Amount, Type = "Decimal", Aggregations = "sum,p95", UnitDimension = Currency }],
            TenantAttributes = [new TenantAttributeOptions { Name = "parent" }, new TenantAttributeOptions { Name = "region" }],
            Rollups =
            [
                new RollupOptions
                {
                    Name = _rollup, Grain = "day", EventTypes = "sale", TenantAttributes = "parent,region",
                    Dimensions = Currency, Measures = $"{Amount}:sum,{Amount}:p95",
                },
            ],
            AliasEventType = "alias",
        };

        _connections = new ClickHouseConnectionFactory(Microsoft.Extensions.Options.Options.Create(_options));
        DimensionRegistry dimensions = new(_options);
        MeasureRegistry measures = new(_options, dimensions);
        _attributes = new TenantAttributeRegistry(_options);
        _rollups = new RollupRegistry(_options, dimensions, measures, _attributes);

        ClickHouseSchemaMigrator migrator = new(
            _connections, dimensions, measures, _rollups, Microsoft.Extensions.Options.Options.Create(_options),
            NullLogger<ClickHouseSchemaMigrator>.Instance, _attributes);
        await migrator.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await ClickHouseTestSupport.ExecuteAsync($"DROP VIEW IF EXISTS nealytics_core.rollup_{_rollup}_mv");
        await ClickHouseTestSupport.ExecuteAsync($"DROP TABLE IF EXISTS nealytics_core.rollup_{_rollup}");
        await ClickHouseTestSupport.ExecuteAsync(
            $"ALTER TABLE nealytics_core.tenant_attributes DELETE WHERE project_id = '{_project}' SETTINGS mutations_sync = 2");
        await ClickHouseTestSupport.DeleteProjectsAsync(_project);
        await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE {Table} DROP COLUMN IF EXISTS {Currency}");
        await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE {Table} DROP COLUMN IF EXISTS {Amount}");
    }

    private TenantSet Org => new() { Attribute = "parent", Value = _org };

    private async Task AssignAsync(params (string Tenant, string Parent, string Region)[] tenants)
    {
        ClickHouseTenantAttributeWriter writer = new(_connections);
        List<TenantAttributeRow> rows = [];

        foreach ((string tenant, string parent, string region) in tenants)
        {
            rows.Add(new TenantAttributeRow { TenantId = tenant, Attribute = "parent", Value = parent });
            rows.Add(new TenantAttributeRow { TenantId = tenant, Attribute = "region", Value = region });
        }

        await writer.WriteAsync(_project, rows, DateTime.UtcNow, CancellationToken.None);
    }

    private static string At(DateTime day, int hour) =>
        day.AddHours(hour).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private async Task InsertAsync(params string[] rows) =>
        await ClickHouseTestSupport.ExecuteAsync(
            $"INSERT INTO {Table} (event_id, project_id, tenant_id, session_id, user_id, object_id, event_type, timestamp, {Currency}, {Amount}) VALUES "
            + string.Join(", ", rows));

    private string Row(
        string tenant, string session, string eventType, string at, string currency = "", string amount = "NULL",
        string userId = "NULL", string objectId = "NULL") =>
        $"(generateUUIDv4(), '{_project}', '{tenant}', '{session}', {Quote(userId)}, {Quote(objectId)}, '{eventType}', "
        + $"toDateTime64('{at}', 3, 'UTC'), '{currency}', {amount})";

    private static string Quote(string value) => value == "NULL" ? "NULL" : $"'{value}'";

    private BreakdownRequest Breakdown(string groupBy, BreakdownMetric metric, DateTime from, DateTime to, string? tenant = null) => new()
    {
        ProjectId = _project,
        TenantId = tenant ?? string.Empty,
        TenantSet = Org,
        Metric = metric,
        MeasureColumn = metric == BreakdownMetric.Measure ? Amount : null,
        MeasureFunction = metric == BreakdownMetric.Measure ? "sum" : null,
        MeasureAggregation = metric == BreakdownMetric.Measure ? "sum" : null,
        MetricWire = metric == BreakdownMetric.Measure ? $"sum({Amount})" : "events",
        GroupByColumn = groupBy,
        EventType = "sale",
        Filters = [QueryFilter.Equals(Currency, "TRY")],
        TrafficClass = "normal",
        From = from,
        To = to,
        Limit = 100,
        Order = BreakdownOrder.KeyAscending,
    };

    private GetBreakdownQuery BreakdownQuery() => new(_connections, _rollups, NullLogger<GetBreakdownQuery>.Instance);

    [Fact]
    public async Task ASetQuery_CountsOnlyTheSetsTenants_AndFollowsReassignment()
    {
        await AssignAsync(("a1", _org, "west"), ("a2", _org, "east"), ("b1", "other-org", "west"));
        await InsertAsync(
            Row("a1", "s1", "sale", At(Day, 10), "TRY", "10"),
            Row("a2", "s2", "sale", At(Day, 11), "TRY", "20"),
            Row("a2", "s3", "sale", At(Day, 12), "TRY", "5"),
            Row("b1", "s4", "sale", At(Day, 13), "TRY", "1000"),
            Row("zz", "s5", "sale", At(Day, 13), "TRY", "1000"));

        BreakdownResponse byTenant = await BreakdownQuery().ExecuteAsync(
            Breakdown("tenant_id", BreakdownMetric.Measure, From.AddSeconds(1), To), CancellationToken.None);

        byTenant.Source.Should().Be("raw");
        byTenant.Rows.Select(r => (r.Key, r.Value)).Should().Equal(("a1", 10d), ("a2", 25d));

        BreakdownResponse byRegion = await BreakdownQuery().ExecuteAsync(
            Breakdown("tenant.region", BreakdownMetric.Measure, From.AddSeconds(1), To), CancellationToken.None);
        byRegion.Rows.Select(r => (r.Key, r.Value)).Should().Equal(("east", 25d), ("west", 10d));

        await AssignAsync(("b1", _org, "west"));

        BreakdownResponse afterMove = await BreakdownQuery().ExecuteAsync(
            Breakdown("tenant_id", BreakdownMetric.Measure, From.AddSeconds(1), To), CancellationToken.None);
        afterMove.Rows.Select(r => r.Key).Should().Equal("a1", "a2", "b1");

        BreakdownResponse narrowed = await BreakdownQuery().ExecuteAsync(
            Breakdown("tenant_id", BreakdownMetric.Measure, From.AddSeconds(1), To, tenant: "zz"), CancellationToken.None);
        narrowed.Rows.Should().BeEmpty("narrowing to a tenant outside the set must not reach it");
    }

    [Fact]
    public async Task AGroupRollup_AgreesWithRaw_IncludingThePercentile()
    {
        await AssignAsync(("a1", _org, "west"), ("a2", _org, "east"), ("b1", "other-org", "west"));
        List<string> rows = [];

        for (int i = 0; i < 40; i++)
        {
            rows.Add(Row(i % 2 == 0 ? "a1" : "a2", $"s{i}", "sale", At(Day, i % 20), i % 5 == 0 ? "EUR" : "TRY", (i + 1).ToString(CultureInfo.InvariantCulture)));
        }

        rows.Add(Row("b1", "sx", "sale", At(Day, 3), "TRY", "999"));
        await InsertAsync([.. rows]);

        BreakdownRequest aligned = Breakdown("tenant.region", BreakdownMetric.Measure, From, To);
        BreakdownResponse routed = await BreakdownQuery().ExecuteAsync(aligned, CancellationToken.None);
        BreakdownResponse raw = await BreakdownQuery().ExecuteAsync(
            Breakdown("tenant.region", BreakdownMetric.Measure, From.AddSeconds(1), To), CancellationToken.None);

        routed.Source.Should().Be("rollup:" + _rollup);
        raw.Source.Should().Be("raw");
        routed.Rows.Select(r => (r.Key, r.Value)).Should().Equal(raw.Rows.Select(r => (r.Key, r.Value)));

        PivotRequest pivot = new()
        {
            ProjectId = _project,
            TenantId = string.Empty,
            TenantSet = Org,
            GroupByColumn = Currency,
            Metrics =
            [
                new PivotMetric { Kind = PivotMetricKind.Measure, Wire = $"p95({Amount})", Column = Amount, AggregationName = "quantile", AggregationParameters = "0.95", CanonicalAggregation = "p95", EventType = "sale" },
                new PivotMetric { Kind = PivotMetricKind.Events, Wire = "events:sale", EventType = "sale" },
            ],
            Filters = [],
            TrafficClass = "normal",
            From = From,
            To = To,
            Limit = 10,
            OrderByKey = true,
        };

        GetPivotQuery pivotQuery = new(_connections, _rollups, NullLogger<GetPivotQuery>.Instance);
        PivotResponse routedPivot = await pivotQuery.ExecuteAsync(pivot, CancellationToken.None);

        routedPivot.Source.Should().Be("rollup:" + _rollup);
        routedPivot.Rows.Select(r => r.Key).Should().Equal("EUR", "TRY");
        routedPivot.Rows[1].Values[0].Should().BeInRange(30, 40, "the 95th percentile of the TRY amounts 2..40 sits near the top");
        routedPivot.Totals[1].Should().Be(40, "the other org's tenant is not in the set");
    }

    [Fact]
    public async Task RetentionCountsReturningUsersPerWeek()
    {
        DateTime week0 = DateTime.UtcNow.Date.AddDays(-(((int)DateTime.UtcNow.DayOfWeek + 6) % 7)).AddDays(-21);
        await AssignAsync(("a1", _org, "west"));
        await InsertAsync(
            Row("a1", "s1", "open", At(week0, 1), userId: "u1"),
            Row("a1", "s2", "open", At(week0, 2), userId: "u2"),
            Row("a1", "s3", "open", At(week0.AddDays(7), 2), userId: "u1"),
            Row("a1", "s4", "open", At(week0.AddDays(14), 2), userId: "u2"),
            Row("a1", "s5", "open", At(week0.AddDays(7), 3), userId: "u3"),
            Row("a1", "s6", "open", At(week0.AddDays(8), 3)));

        RetentionRequest request = new()
        {
            ProjectId = _project,
            TenantId = string.Empty,
            TenantSet = Org,
            Period = RetentionPeriod.Week,
            Actor = RetentionActor.Users,
            Filters = [],
            TrafficClass = "normal",
            From = week0,
            To = week0.AddDays(20),
            PeriodCount = 3,
        };

        RetentionResponse response = await new GetRetentionQuery(_connections, NullLogger<GetRetentionQuery>.Instance)
            .ExecuteAsync(request, CancellationToken.None);

        response.Cohorts.Should().HaveCount(2);
        response.Cohorts[0].Cohort.Should().Be(week0);
        response.Cohorts[0].Returning.Should().Equal(2, 1, 1);
        response.Cohorts[0].Rates.Should().Equal(1, 0.5, 0.5);
        response.Cohorts[1].Returning.Should().Equal(1, 0);
    }

    [Fact]
    public async Task AStitchedFunnel_FollowsASessionThroughAnEntityToAUser()
    {
        await InsertAsync(
            Row("a1", "anon-1", "menu_view", At(Day, 10)),
            Row("a1", "anon-1", "alias", At(Day, 10).Replace(":00:00", ":01:00"), objectId: "order-1"),
            Row("a1", "order-1", "alias", At(Day, 10).Replace(":00:00", ":02:00"), userId: "guest-1"),
            Row("a1", "app-9", "order", At(Day, 10).Replace(":00:00", ":05:00"), userId: "guest-1"),
            Row("a1", "anon-2", "menu_view", At(Day, 11)));

        FunnelRequest request = new()
        {
            ProjectId = _project,
            TenantId = "a1",
            Steps = [new FunnelStep { EventType = "menu_view" }, new FunnelStep { EventType = "order" }],
            WindowSeconds = 3600,
            From = From,
            To = To,
            Limit = 10,
        };

        GetFunnelQuery query = new(_connections, NullLogger<GetFunnelQuery>.Instance);

        FunnelResponse bySession = await query.ExecuteAsync(request with { Grain = FunnelGrain.Sessions }, CancellationToken.None);
        FunnelResponse byIdentity = await query.ExecuteAsync(
            request with { Grain = FunnelGrain.Identities, AliasEventType = "alias" }, CancellationToken.None);

        bySession.Steps.Select(s => s.Count).Should().Equal(2, 0);
        byIdentity.Grain.Should().Be("identity");
        byIdentity.Steps.Select(s => s.Count).Should().Equal(2, 1);
    }

    [Fact]
    public async Task AStitchedTimeline_ShowsAGuestsAnonymousHistory()
    {
        await InsertAsync(
            Row("a1", "anon-1", "menu_view", At(Day, 10)),
            Row("a1", "anon-1", "alias", At(Day, 11), objectId: "order-1"),
            Row("a1", "order-1", "alias", At(Day, 12), userId: "guest-1"),
            Row("a1", "app-9", "order", At(Day, 13), userId: "guest-1"),
            Row("a1", "anon-2", "menu_view", At(Day, 14)));

        GetProjectTimelineQuery query = new(
            _connections, new DimensionRegistry(_options),
            new MeasureRegistry(_options, new DimensionRegistry(_options)), NullLogger<GetProjectTimelineQuery>.Instance);

        TimelineQueryRequest plain = new() { ProjectId = _project, TenantId = "a1", Limit = 50, UserId = "guest-1" };
        ProjectTimelineResponse identified = await query.ExecuteAsync(plain, CancellationToken.None);
        ProjectTimelineResponse stitched = await query.ExecuteAsync(plain with { StitchedAliasEventType = "alias" }, CancellationToken.None);

        identified.Events.Select(e => e.SessionId).Should().BeEquivalentTo(["app-9", "order-1"]);
        stitched.Events.Select(e => e.SessionId).Should().BeEquivalentTo(["app-9", "order-1", "anon-1", "anon-1"]);
    }

    [Fact]
    public async Task ASetOfAThousandTenants_IsAnsweredWithinBudget_AndPrunedByTheSortKey()
    {
        const int inSet = 1000;
        const int others = 2000;
        const int perTenant = 60;
        List<TenantAttributeRow> attributes = new(inSet + others);

        for (int i = 0; i < inSet + others; i++)
        {
            attributes.Add(new TenantAttributeRow { TenantId = $"v{i}", Attribute = "parent", Value = i < inSet ? _org : $"o{i % 50}" });
        }

        await new ClickHouseTenantAttributeWriter(_connections).WriteAsync(_project, attributes, DateTime.UtcNow, CancellationToken.None);

        await ClickHouseTestSupport.ExecuteAsync(
            $"INSERT INTO {Table} (event_id, project_id, tenant_id, session_id, event_type, timestamp, {Currency}, {Amount}) "
            + $"SELECT generateUUIDv4(), '{_project}', concat('v', toString(number % {inSet + others})), concat('s', toString(intDiv(number, 3))), "
            + $"'sale', toDateTime64('{At(Day, 0)}', 3, 'UTC') + toIntervalSecond(number % 86000), 'TRY', toDecimal64(number % 100, 2) "
            + $"FROM numbers({(inSet + others) * perTenant})");

        BreakdownRequest request = Breakdown("tenant_id", BreakdownMetric.Measure, From.AddSeconds(1), To);
        await BreakdownQuery().ExecuteAsync(request, CancellationToken.None);

        Stopwatch stopwatch = Stopwatch.StartNew();
        BreakdownResponse response = await BreakdownQuery().ExecuteAsync(request with { Limit = 2000 }, CancellationToken.None);
        stopwatch.Stop();

        response.GroupCount.Should().Be(inSet);
        response.Rows.Should().HaveCount(inSet);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(1500, "the plan's budget for an org-wide question over 1,000 venues is 1.5 s");

        await using ClickHouseConnection connection = new(ClickHouseTestSupport.ConnectionString);
        await connection.OpenAsync();
        await using ClickHouseCommand explain = connection.CreateCommand(
            $"EXPLAIN indexes = 1 SELECT count() FROM {Table} WHERE project_id = '{_project}' AND tenant_id IN "
            + $"(SELECT tenant_id FROM nealytics_core.tenant_attributes FINAL WHERE project_id = '{_project}' AND attribute = 'parent' AND value = '{_org}')");
        StringBuilder plan = new();
        await using (System.Data.Common.DbDataReader reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.AppendLine(reader.GetString(0));
            }
        }

        string text = plan.ToString();
        int keyAt = text.IndexOf("PrimaryKey", StringComparison.Ordinal);
        keyAt.Should().BeGreaterThan(0, text);
        text[keyAt..].Should().Contain("tenant_id in", "the set is applied to the primary key, not after reading");
    }

    public sealed class ServerKeyFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.Configure<TelemetryEngineOptions>(options =>
            {
                options.IngestionKeys = [new IngestionKeyOptions { Key = "org-server-key", Scope = "server" }];
                options.ServerEventTypes = "sale";
            }));
        }
    }

    [Fact]
    public async Task ASessionlessServerEvent_IsStored_AndABrowserKeyCannotForgeIt()
    {
        await using ServerKeyFactory factory = new();
        HttpClient client = factory.CreateClient();

        async Task<HttpStatusCode> TrackAsync(string key, string json)
        {
            HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/telemetry/track")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Project-Key", key);
            return (await client.SendAsync(request)).StatusCode;
        }

        (await TrackAsync("test-key-1", $$"""{"projectId":"{{_project}}","tenantId":"a1","sessionId":"s","eventType":"sale"}"""))
            .Should().Be(HttpStatusCode.BadRequest);
        (await TrackAsync("org-server-key", $$"""{"projectId":"{{_project}}","tenantId":"a1","eventType":"sale","objectId":"order-7"}"""))
            .Should().Be(HttpStatusCode.Accepted);

        long stored = 0;

        for (int i = 0; i < 30 && stored == 0; i++)
        {
            await Task.Delay(500);
            stored = await ClickHouseTestSupport.CountAsync(_project);
        }

        stored.Should().Be(1);
    }
}

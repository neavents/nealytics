using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Nealytics.Engine.Features.GetComparison;
using Nealytics.Engine.Features.GetEventTimeSeries;
using Nealytics.Engine.Features.GetUnseenObjects;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class CompareSeriesUnseenIntegrationTests : IntegrationTestBase, IAsyncLifetime
{
    private const string Measure = "probe_ms";

    private readonly string _project = $"p-reads-{Guid.NewGuid():N}";
    private readonly string _tenant = $"t-{Guid.NewGuid():N}";
    private readonly string _foreign = $"t-{Guid.NewGuid():N}";
    private readonly string _rollup = $"cmp_{Guid.NewGuid():N}"[..20];

    private static readonly DateTime Today = DateTime.UtcNow.Date;
    private static readonly DateTime CurrentFrom = Today.AddDays(-7);
    private static readonly DateTime PreviousFrom = Today.AddDays(-14);

    private TelemetryEngineOptions _options = null!;
    private ClickHouseConnectionFactory _connections = null!;
    private RollupRegistry _rollups = null!;
    private QueryColumns _columns = null!;
    private MeasureRegistry _measures = null!;

    public CompareSeriesUnseenIntegrationTests(TestWebApplicationFactory factory) : base(factory) { }

    public async Task InitializeAsync()
    {
        _options = new TelemetryEngineOptions
        {
            ClickHouseConnectionString = ClickHouseTestSupport.ConnectionString,
            ConnectionPoolSize = 2,
            Measures = [new MeasureOptions { Name = Measure, Type = "UInt32", Aggregations = "sum,avg,p95" }],
            Rollups =
            [
                new RollupOptions { Name = _rollup, Grain = "day", EventTypes = "", Dimensions = "object_id", Measures = Measure + ":avg" },
            ],
        };

        _connections = new ClickHouseConnectionFactory(Microsoft.Extensions.Options.Options.Create(_options));
        DimensionRegistry dimensions = new(_options);
        _measures = new MeasureRegistry(_options, dimensions);
        _rollups = new RollupRegistry(_options, dimensions, _measures);
        _columns = new QueryColumns(dimensions);

        ClickHouseSchemaMigrator migrator = new(
            _connections, dimensions, _measures, _rollups,
            Microsoft.Extensions.Options.Options.Create(_options),
            NullLogger<ClickHouseSchemaMigrator>.Instance);
        await migrator.StartAsync(CancellationToken.None);

        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await ClickHouseTestSupport.DeleteProjectsAsync(_project);
        await ClickHouseTestSupport.ExecuteAsync($"DROP VIEW IF EXISTS nealytics_core.rollup_{_rollup}_mv");
        await ClickHouseTestSupport.ExecuteAsync($"DROP TABLE IF EXISTS nealytics_core.rollup_{_rollup}");

        long remaining = await ScalarAsync($"SELECT count() FROM nealytics_core.global_events WHERE {Measure} IS NOT NULL");

        if (remaining == 0)
        {
            await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.global_events DROP COLUMN IF EXISTS {Measure}");
        }

        await _connections.DisposeAsync();
    }

    private static string At(DateTime day, int hour) =>
        day.AddHours(hour).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private string Row(string tenant, string session, string eventType, string objectId, DateTime day, int hour, int? probe, string traffic = "normal") =>
        $"(generateUUIDv4(), '{_project}', '{tenant}', '{session}', '{eventType}', '{objectId}', "
        + $"toDateTime64('{At(day, hour)}', 3, 'UTC'), '{traffic}', {(probe is int value ? value.ToString(CultureInfo.InvariantCulture) : "NULL")})";

    private async Task SeedAsync()
    {
        DateTime previous = Today.AddDays(-10);
        DateTime current = Today.AddDays(-3);

        string rows = string.Join(", ",
            Row(_tenant, "s1", "view", "a", previous, 9, 100),
            Row(_tenant, "s1", "view", "b", previous, 10, 300),
            Row(_tenant, "s2", "view", "a", previous, 11, 200),
            Row(_tenant, "s3", "view", "a", current, 9, 400),
            Row(_tenant, "s4", "view", "a", current, 10, 600),
            Row(_tenant, "s4", "view", "c", current, 11, null),
            Row(_tenant, "s5", "impression", "d", current, 12, null),
            Row(_tenant, "s5", "view", "a", Today.AddDays(-2), 9, 800),
            Row(_tenant, "s6", "view", "c", Today.AddDays(-5), 9, null),
            Row(_tenant, "bot", "view", "f", current, 13, 5000, "bot"),
            Row(_foreign, "x1", "view", "e", current, 9, 9000));

        await ClickHouseTestSupport.ExecuteAsync(
            "INSERT INTO nealytics_core.global_events "
            + $"(event_id, project_id, tenant_id, session_id, event_type, object_id, timestamp, traffic_class, {Measure}) VALUES " + rows);
    }

    private static async Task<long> ScalarAsync(string sql)
    {
        await using ClickHouseConnection connection = new(ClickHouseTestSupport.ConnectionString);
        await connection.OpenAsync();
        await using ClickHouseCommand command = connection.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static string Iso(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private QueryGuard Guard(int seconds = 30) => new(new TelemetryEngineOptions { QueryExecutionTimeoutSeconds = seconds });

    private ComparisonRequest Comparison(string metric, string? groupBy = null, string? orderBy = null) =>
        ComparisonRequestFactory.Create(
            new ComparisonQueryParameters
            {
                ProjectId = _project,
                TenantId = _tenant,
                Metrics = [metric],
                GroupBy = groupBy,
                Filters = [],
                From = Iso(CurrentFrom),
                To = Iso(Today),
                OrderBy = orderBy,
            },
            _columns, _measures, 100, 24, DateTime.UtcNow).Request;

    private GetComparisonQuery ComparisonQuery() =>
        new(_connections, Guard(), _rollups, NullLogger<GetComparisonQuery>.Instance);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, url) { Content = content };
        request.Headers.Add("Authorization", $"Bearer {GetJwt(_project, _tenant)}");
        return await Client.SendAsync(request);
    }

    [Fact]
    public async Task CompareOverHttp_GroupsBothWindowsAndStaysInsideTheTokenTenant()
    {
        HttpResponseMessage response = await SendAsync(HttpMethod.Get,
            $"/api/v1/analytics/compare?metric=events:view&groupBy=object_id&from={Iso(CurrentFrom)}&to={Iso(Today)}");
        string text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(text);

        body.GetProperty("source").GetString().Should().Be("raw");
        body.GetProperty("previous").GetProperty("from").GetDateTime().Should().Be(PreviousFrom);
        body.GetProperty("previous").GetProperty("to").GetDateTime().Should().Be(CurrentFrom);
        body.GetProperty("totals").GetProperty("current").GetDouble().Should().Be(5);
        body.GetProperty("totals").GetProperty("previous").GetDouble().Should().Be(3);
        body.GetProperty("groupCount").GetInt64().Should().Be(3);

        JsonElement rows = body.GetProperty("rows");
        rows.EnumerateArray().Select(r => r.GetProperty("key").GetString()).Should().Equal("a", "c", "b");
        rows[0].GetProperty("value").GetProperty("current").GetDouble().Should().Be(3);
        rows[0].GetProperty("value").GetProperty("previous").GetDouble().Should().Be(2);
        rows[0].GetProperty("value").GetProperty("relativeChange").GetDouble().Should().Be(0.5);
        rows[1].GetProperty("value").GetProperty("relativeChange").ValueKind.Should().Be(JsonValueKind.Null);
        rows[2].GetProperty("value").GetProperty("change").GetDouble().Should().Be(-1);
    }

    [Fact]
    public async Task CompareRejectsARangeBeyondTheGuardAsAProblem()
    {
        HttpResponseMessage response = await SendAsync(HttpMethod.Get,
            $"/api/v1/analytics/compare?metric=events&from={Iso(Today.AddDays(-120))}&to={Iso(Today)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task CompareOfSessionsCountsDistinctVisitsPerWindow()
    {
        ComparisonResponse response = await ComparisonQuery().ExecuteAsync(Comparison("sessions:view"), CancellationToken.None);

        response.Totals.Current.Should().Be(4);
        response.Totals.Previous.Should().Be(2);
        response.Totals.RelativeChange.Should().Be(1);
        response.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task CompareOfAMeasureRoutesToTheRollupAndAgreesWithRaw()
    {
        ComparisonRequest request = Comparison("avg(probe_ms):view", groupBy: "object_id");

        ComparisonResponse routed = await ComparisonQuery().ExecuteAsync(request, CancellationToken.None);
        _rollups.MarkUnroutable(_rollups.Declared[0], "test");
        ComparisonResponse raw = await ComparisonQuery().ExecuteAsync(request, CancellationToken.None);

        routed.Source.Should().Be("rollup:" + _rollup);
        raw.Source.Should().Be("raw");
        raw.Totals.Current.Should().Be(600);
        raw.Totals.Previous.Should().Be(200);
        raw.Totals.RelativeChange.Should().Be(2);
        routed.Totals.Current.Should().Be(raw.Totals.Current);
        routed.Totals.Previous.Should().Be(raw.Totals.Previous);
        routed.Rows.Select(r => (r.Key, r.Value.Current, r.Value.Previous))
            .Should().Equal(raw.Rows.Select(r => (r.Key, r.Value.Current, r.Value.Previous)));
        raw.Rows.Single(r => r.Key == "c").Value.Current.Should().BeNull("c has events but no measured value");
    }

    [Fact]
    public async Task MeasureTimeSeriesAggregatesPerBucketAndLeavesAnUnmeasuredBucketNull()
    {
        EventTimeSeriesRequest request = EventTimeSeriesRequestFactory.Create(
            _project, _tenant, null, "day", Iso(PreviousFrom), Iso(Today), "view", null, null, null,
            [], "avg(probe_ms)", _columns, _measures, 100, 24, DateTime.UtcNow).Request;

        EventTimeSeriesResponse response = await new GetEventTimeSeriesQuery(
            _connections, Guard(), NullLogger<GetEventTimeSeriesQuery>.Instance).ExecuteAsync(request, CancellationToken.None);

        response.Metric.Should().Be("avg(probe_ms)");
        response.Points.Select(p => (p.Bucket, p.Count, p.Value)).Should().Equal(
            (Today.AddDays(-10), 3L, (double?)200),
            (Today.AddDays(-5), 1L, (double?)null),
            (Today.AddDays(-3), 3L, (double?)500),
            (Today.AddDays(-2), 1L, (double?)800));
        response.TotalCount.Should().Be(8);
    }

    [Fact]
    public async Task MeasureTimeSeriesOverHttpRefusesAMeasureTheDeploymentDidNotDeclare()
    {
        HttpResponseMessage response = await SendAsync(HttpMethod.Get,
            $"/api/v1/analytics/timeseries?interval=day&metric=avg(probe_ms)&from={Iso(PreviousFrom)}&to={Iso(Today)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SessionsTimeSeriesOverHttpReportsAValuePerBucket()
    {
        HttpResponseMessage response = await SendAsync(HttpMethod.Get,
            $"/api/v1/analytics/timeseries?interval=day&metric=sessions&eventType=view&from={Iso(PreviousFrom)}&to={Iso(Today)}");
        string text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);

        JsonElement points = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("points");
        points.EnumerateArray().Select(p => p.GetProperty("value").GetDouble()).Should().Equal(2, 1, 2, 1);
    }

    private static string[] CandidatesAtTheCap()
    {
        string[] named = ["a", "b", "c", "d", "e", "f"];
        return [.. named, .. Enumerable.Range(0, UnseenObjectsRequestFactory.MaxIds - named.Length).Select(i => $"x{i:D4}")];
    }

    [Fact]
    public async Task UnseenAtTheCapSeparatesNeverSeenFromImpressionOnly()
    {
        string[] ids = CandidatesAtTheCap();
        string payload = JsonSerializer.Serialize(new
        {
            ids,
            from = Iso(PreviousFrom),
            to = Iso(Today),
            eventType = "view",
            impressionEventType = "impression",
        });

        HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/analytics/unseen",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        string text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        JsonElement body = JsonSerializer.Deserialize<JsonElement>(text);

        body.GetProperty("candidates").GetInt32().Should().Be(5_000);
        body.GetProperty("seen").GetInt32().Should().Be(3);
        body.GetProperty("impressionOnly").EnumerateArray().Select(v => v.GetString()).Should().Equal("d");
        string[] unseen = body.GetProperty("unseen").EnumerateArray().Select(v => v.GetString()!).ToArray();
        unseen.Should().HaveCount(4_996);
        unseen.Should().Contain(["e", "f"], "a foreign tenant's view and a bot's view are not sightings");
        unseen.Should().NotContain(["a", "b", "c", "d"]);
        unseen.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public async Task UnseenWithoutAnEventTypeCountsAnyEventAsASighting()
    {
        HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/v1/analytics/unseen",
            JsonContent.Create(new { ids = new[] { "a", "d", "e" }, from = Iso(PreviousFrom), to = Iso(Today) }));
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("unseen").EnumerateArray().Select(v => v.GetString()).Should().Equal("e");
        body.GetProperty("impressionOnly").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task UnseenRefusesMoreThanTheCapAndAMalformedBody()
    {
        string[] tooMany = [.. CandidatesAtTheCap(), "one-more"];

        HttpResponseMessage over = await SendAsync(HttpMethod.Post, "/api/v1/analytics/unseen",
            JsonContent.Create(new { ids = tooMany }));
        HttpResponseMessage malformed = await SendAsync(HttpMethod.Post, "/api/v1/analytics/unseen",
            new StringContent("{\"ids\": [", Encoding.UTF8, "application/json"));

        over.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnseenRoutedToTheRollupAgreesWithRaw()
    {
        UnseenObjectsRequest request = UnseenObjectsRequestFactory.Create(
            _project, _tenant,
            new UnseenObjectsBody
            {
                Ids = ["a", "b", "c", "d", "e"],
                From = Iso(PreviousFrom),
                To = Iso(Today),
                EventType = "view",
                ImpressionEventType = "impression",
            },
            _columns, _measures, 24, DateTime.UtcNow).Request;
        GetUnseenObjectsQuery query = new(_connections, Guard(), _rollups, NullLogger<GetUnseenObjectsQuery>.Instance);

        UnseenObjectsResponse routed = await query.ExecuteAsync(request, CancellationToken.None);
        _rollups.MarkUnroutable(_rollups.Declared[0], "test");
        UnseenObjectsResponse raw = await query.ExecuteAsync(request, CancellationToken.None);

        routed.Source.Should().Be("rollup:" + _rollup);
        raw.Source.Should().Be("raw");
        routed.Unseen.Should().Equal(raw.Unseen).And.Equal("e");
        routed.ImpressionOnly.Should().Equal(raw.ImpressionOnly).And.Equal("d");
    }

    private async Task<long> RowsReadAsync(string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters, ClickHouseTableProvider? table)
    {
        string queryId = Guid.NewGuid().ToString();

        await using (ClickHouseConnection connection = new(ClickHouseTestSupport.ConnectionString))
        {
            await connection.OpenAsync();
            await using ClickHouseCommand command = connection.CreateCommand(Guard().Limit(sql));
            command.QueryId = queryId;

            foreach (KeyValuePair<string, object?> parameter in parameters)
            {
                command.Parameters.Add(new ClickHouseParameter { ParameterName = parameter.Key, Value = parameter.Value });
            }

            if (table is not null)
            {
                command.TableProviders.Add(table);
            }

            await using System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
            }
        }

        await ClickHouseTestSupport.ExecuteAsync("SYSTEM FLUSH LOGS");
        return await ScalarAsync(
            $"SELECT read_rows FROM system.query_log WHERE query_id = '{queryId}' AND type = 'QueryFinish'");
    }

    [Fact]
    public async Task TheNewReadsTouchOnlyTheTenantsGranules()
    {
        const int noise = 400_000;
        await ClickHouseTestSupport.ExecuteAsync(
            "INSERT INTO nealytics_core.global_events (event_id, project_id, tenant_id, session_id, event_type, object_id, timestamp, traffic_class) "
            + $"SELECT generateUUIDv4(), '{_project}', 'noise-' || toString(number % 7), toString(number % 5000), "
            + $"if(number % 3 = 0, 'impression', 'view'), 'x' || leftPad(toString(number % 5000), 4, '0'), "
            + $"toDateTime64('{At(Today.AddDays(-4), 0)}', 3, 'UTC') + toIntervalSecond(number % 86400), 'normal' FROM numbers({noise})");

        UnseenObjectsRequest unseen = UnseenObjectsRequestFactory.Create(
            _project, _tenant,
            new UnseenObjectsBody { Ids = CandidatesAtTheCap(), From = Iso(PreviousFrom), To = Iso(Today), EventType = "view", ImpressionEventType = "impression" },
            _columns, _measures, 24, DateTime.UtcNow).Request;
        (string unseenSql, IReadOnlyList<KeyValuePair<string, object?>> unseenParameters) = GetUnseenObjectsQuery.BuildQuery(unseen, null);

        ComparisonRequest compare = Comparison("events:view", groupBy: "object_id");
        (string compareSql, IReadOnlyList<KeyValuePair<string, object?>> compareParameters) = GetComparisonQuery.BuildQuery(compare, null);

        long unseenRows = await RowsReadAsync(unseenSql, unseenParameters, GetUnseenObjectsQuery.Candidates(unseen.Ids));
        long compareRows = await RowsReadAsync(compareSql, compareParameters, null);

        unseenRows.Should().BeLessThan(noise / 10, "the primary key prunes to one tenant's granules");
        compareRows.Should().BeLessThan(noise / 10, "the primary key prunes to one tenant's granules");
    }

    [Fact]
    public async Task AQueryPastTheExecutionLimitIsClassifiedAsATimeoutAndAnsweredWith422()
    {
        Exception? caught = null;

        try
        {
            await using ClickHouseConnection connection = new(ClickHouseTestSupport.ConnectionString);
            await connection.OpenAsync();
            await using ClickHouseCommand command = connection.CreateCommand(
                Guard(seconds: 1).Limit("SELECT count() FROM system.numbers WHERE NOT ignore(sipHash64(number))"));
            await command.ExecuteScalarAsync();
        }
        catch (Exception exception)
        {
            caught = exception;
        }

        caught.Should().NotBeNull();
        ClickHouseTimeoutFault.IsTimeout(caught!).Should().BeTrue(caught!.ToString());

        QueryTimeoutMiddleware middleware = new(_ => Task.FromException(caught), Guard(seconds: 1));
        DefaultHttpContext context = new() { RequestServices = Factory.Services };
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(422);
        context.Response.ContentType.Should().Be("application/problem+json");
    }
}

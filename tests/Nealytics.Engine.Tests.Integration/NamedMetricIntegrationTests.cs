using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class NamedMetricIntegrationTests : IAsyncLifetime
{
    private readonly string _project = "c1_" + Guid.NewGuid().ToString("N");
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private Factory _factory = null!;
    private HttpClient _client = null!;
    private string Amount => "c1_amount_" + _suffix;
    private string Units => "c1_units_" + _suffix;
    private string Floating => "c1_float_" + _suffix;
    private string DecimalAmount => "c1_decimal_" + _suffix;

    public async Task InitializeAsync()
    {
        await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.global_events"
            + $" ADD COLUMN {Amount} Nullable(Int64), ADD COLUMN {Units} Nullable(String),"
            + $" ADD COLUMN {Floating} Nullable(Float64), ADD COLUMN {DecimalAmount} Nullable(Decimal(18,4))");
        await ClickHouseTestSupport.ExecuteAsync(TenantAttributeRegistry.BuildTableDdl(""));
        await ClickHouseTestSupport.ExecuteAsync($"INSERT INTO nealytics_core.tenant_attributes VALUES"
            + $" ('{_project}','cohort','t','group',now64(3)), ('{_project}','cohort','other','away',now64(3)),"
            + $" ('{_project}_other','cohort','other','group',now64(3))");
        string Row(string project, string tenant, string type, string traffic, string time, string amount, string unit, string number, string fraction) =>
            $"(generateUUIDv4(),'{project}','{tenant}','s','{type}','{traffic}','{time}',{amount},'{unit}',{number},{fraction})";
        string maximum = "9223372036854775807";
        string huge = "1.7976931348623157e308";
        string[] rows =
        [
            Row(_project, "t", "action", "normal", "2090-01-01 00:00:00.000", maximum, "u", huge, "0.1001"),
            Row(_project, "t", "action", "normal", "2090-01-01 00:00:01.999", maximum, "u", huge, "0.2002"),
            Row(_project, "t", "action", "normal", "2090-01-01 00:00:02.000", "5", "u", "0", "5"),
            Row(_project, "t", "action", "normal", "2089-12-31 23:59:59.999", "6", "u", "0", "6"),
            Row(_project, "t", "action", "normal", "2090-01-01 00:00:01.000", "7", "v", "NULL", "0.7003"),
            Row(_project, "t", "action", "normal", "2090-01-01 00:00:01.000", "NULL", "u", "NULL", "NULL"),
            Row(_project, "t", "action", "bot", "2090-01-01 00:00:01.000", "9", "u", "0", "9"),
            Row(_project, "t", "different", "normal", "2090-01-01 00:00:01.000", "10", "u", "0", "10"),
            Row(_project, "other", "action", "normal", "2090-01-01 00:00:01.000", "11", "u", "0", "11"),
            Row(_project + "_other", "t", "action", "normal", "2090-01-01 00:00:01.000", "12", "u", "0", "12")
        ];
        await ClickHouseTestSupport.ExecuteAsync("INSERT INTO nealytics_core.global_events"
            + $" (event_id,project_id,tenant_id,session_id,event_type,traffic_class,timestamp,{Amount},{Units},{Floating},{DecimalAmount}) VALUES "
            + string.Join(',', rows));
        var options = new TelemetryEngineOptions
        {
            Dimensions = [new() { Name = Units }],
            Measures =
            [
                new() { Name = Amount, Type = "Int64", UnitDimension = Units },
                new() { Name = Floating, Type = "Float64" },
                new() { Name = DecimalAmount, Type = "Decimal" }
            ],
            TenantAttributes = [new() { Name = "cohort" }],
            NamedMetrics = [new() { Name = "events", EventType = "action", Unit = "records" }]
        };
        foreach (string aggregation in new[] { "count", "sum", "avg", "min", "max" })
        {
            options.NamedMetrics.Add(new() { Name = aggregation + "_amount", Measure = Amount, Aggregation = aggregation, EventType = "action" });
        }
        options.NamedMetrics.Add(new() { Name = "sum_float", Measure = Floating, Aggregation = "sum", EventType = "action" });
        options.NamedMetrics.Add(new() { Name = "sum_decimal", Measure = DecimalAmount, Aggregation = "sum", EventType = "action" });
        _factory = new Factory(options);
        _client = _factory.CreateClient();
        _client.BaseAddress = new UriBuilder(_client.BaseAddress!) { Host = IPAddress.Loopback.ToString() }.Uri;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        await ClickHouseTestSupport.DeleteProjectsAsync(_project, _project + "_other");
        await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.tenant_attributes DELETE WHERE project_id IN ('{_project}','{_project}_other') SETTINGS mutations_sync=2");
        await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.global_events"
            + $" DROP COLUMN {Amount}, DROP COLUMN {Units}, DROP COLUMN {Floating}, DROP COLUMN {DecimalAmount}");
    }

    [Theory]
    [InlineData("events", "", "4")]
    [InlineData("count_amount", "", "3")]
    [InlineData("sum_amount", "u", "18446744073709551614")]
    [InlineData("avg_amount", "v", "7")]
    [InlineData("min_amount", "u", "9223372036854775807")]
    [InlineData("max_amount", "u", "9223372036854775807")]
    [InlineData("sum_decimal", "", "1.0006")]
    [InlineData("sum_float", "", null)]
    public async Task PublishedHttpShapeReturnsScopedReferenceResults(string name, string unit, string? expected)
    {
        using var response = await GetAsync(name + (unit.Length == 0 ? "" : "&filter=" + Units + ":" + unit));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, document.RootElement.GetProperty("value").GetString());
        Assert.Equal(name, document.RootElement.GetProperty("name").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("raw", document.RootElement.GetProperty("source").GetString());
        Assert.Equal("string", document.RootElement.GetProperty("numericEncoding").GetString());
        Assert.Equal(new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc), document.RootElement.GetProperty("from").GetDateTime());
        Assert.Equal(new DateTime(2090, 1, 1, 0, 0, 2, DateTimeKind.Utc), document.RootElement.GetProperty("to").GetDateTime());
        if (name == "events")
        {
            Assert.Equal("records", document.RootElement.GetProperty("unit").GetString());
        }
    }

    [Theory]
    [InlineData("count_amount", "0")]
    [InlineData("events", "0")]
    [InlineData("sum_amount", "0")]
    [InlineData("sum_decimal", "0")]
    [InlineData("sum_float", "0")]
    [InlineData("avg_amount", null)]
    [InlineData("min_amount", null)]
    [InlineData("max_amount", null)]
    public async Task EmptySelectionsHaveDefinedZeroOrNullResults(string name, string? expected)
    {
        using var response = await GetAsync(name + "&filter=" + Units + ":missing");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, document.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task MeasureUnitRequirementsAndParameterValuesCannotBeBypassed()
    {
        using var unsafeUnits = await GetAsync("sum_amount");
        Assert.Equal(HttpStatusCode.BadRequest, unsafeUnits.StatusCode);
        using var injected = await GetAsync("events&filter=" + Units + ":" + Uri.EscapeDataString("u' OR 1=1 --"));
        Assert.Equal(HttpStatusCode.OK, injected.StatusCode);
        using var json = JsonDocument.Parse(await injected.Content.ReadAsStringAsync());
        Assert.Equal("0", json.RootElement.GetProperty("value").GetString());
        using var bots = await GetAsync("events&traffic=bot");
        using var botJson = JsonDocument.Parse(await bots.Content.ReadAsStringAsync());
        Assert.Equal("1", botJson.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task DiscoveryPreservesLegacyMetricsAndDoesNotExposeCompiledImplementationFields()
    {
        using var request = Authorized("/api/v1/schema", "t", "schema");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(document.RootElement.GetProperty("metrics").EnumerateArray(), m => m.GetString() == "events");
        var metrics = document.RootElement.GetProperty("namedMetrics");
        Assert.Equal(8, metrics.GetArrayLength());
        var sum = metrics.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "sum_amount");
        Assert.Equal(Units, sum.GetProperty("unitDimension").GetString());
        Assert.Equal(Amount, sum.GetProperty("measure").GetString());
        Assert.Equal("sum", sum.GetProperty("aggregation").GetString());
        Assert.False(sum.TryGetProperty("widenSum", out _));
    }

    [Fact]
    public async Task TenantSetMembershipAndNarrowingIntersectWithProjectScope()
    {
        using var members = await GetAsync("events", null);
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
        using var json = JsonDocument.Parse(await members.Content.ReadAsStringAsync());
        Assert.Equal("4", json.RootElement.GetProperty("value").GetString());
        using var narrowed = await GetAsync("events&tenant=other", null);
        using var narrowJson = JsonDocument.Parse(await narrowed.Content.ReadAsStringAsync());
        Assert.Equal("0", narrowJson.RootElement.GetProperty("value").GetString());
    }

    private async Task<HttpResponseMessage> GetAsync(string name, string? tenant = "t")
    {
        int split = name.IndexOf('&');
        string metric = split < 0 ? name : name[..split];
        string extra = split < 0 ? "" : name[split..];
        using var request = Authorized("/api/v1/analytics/metrics/" + metric
            + "?from=2090-01-01T00:00:00Z&to=2090-01-01T00:00:02Z" + extra, tenant, "metrics");
        return await _client.SendAsync(request);
    }

    private HttpRequestMessage Authorized(string path, string? tenant, string scope)
    {
        List<Claim> claims = [new("project_id", _project), new("scope", scope)];
        claims.Add(tenant is null ? new("tenant_set", "cohort:group") : new("tenant_id", tenant));
        var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes("this-is-a-test-jwt-key-at-least-32-bytes-long!!")), SecurityAlgorithms.HmacSha256));
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }

    private sealed class Factory : TestWebApplicationFactory
    {
        private readonly TelemetryEngineOptions _declared;
        private readonly string _directory;

        public Factory(TelemetryEngineOptions declared)
        {
            _declared = declared;
            _directory = Environment.GetEnvironmentVariable("TelemetryEngine__WriteAheadLogDirectory")!;
            UseKestrel(0);
            ClientOptions.AllowAutoRedirect = false;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var dimensions = new DimensionRegistry(_declared);
                var measures = new MeasureRegistry(_declared, dimensions);
                var attributes = new TenantAttributeRegistry(_declared);
                services.AddSingleton(dimensions);
                services.AddSingleton(measures);
                services.AddSingleton(attributes);
                services.AddSingleton(new QueryColumns(dimensions, attributes));
                services.AddSingleton(new NamedMetricRegistry(_declared, measures));
                services.PostConfigure<TelemetryEngineOptions>(options => options.ReadScopeClaim = "scope");
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)
                    && (d.ImplementationType == typeof(ClickHouseSchemaMigrator)
                        || d.ImplementationType == typeof(TelemetryBatchProcessor))).ToArray())
                {
                    services.Remove(descriptor);
                }
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
    }
}

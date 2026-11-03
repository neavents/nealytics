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
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage.Delivery;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class AnalyticsPlanningIntegrationTests : IAsyncLifetime, IClassFixture<AnalyticsPlanningIntegrationTests.Fixture>
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];
    private string Project => "c8_" + _suffix;
    private readonly Fixture _fixture;
    private string Amount => _fixture.Amount;
    private string Integer => _fixture.Integer;
    private string Tag => _fixture.Tag;
    private string Nullable => _fixture.Nullable;
    private readonly Factory _factory;
    private readonly HttpClient _client;

    public AnalyticsPlanningIntegrationTests(Fixture fixture)
    {
        _fixture = fixture;
        _factory = fixture.Host;
        _client = fixture.Client;
    }

    public async Task InitializeAsync()
    {
        await ClickHouseTestSupport.ExecuteAsync(TenantAttributeRegistry.BuildTableDdl(""));
        await ClickHouseTestSupport.ExecuteAsync($"INSERT INTO nealytics_core.tenant_attributes VALUES ('{Project}','cohort','t','group',now64(3)), ('{Project}','cohort','other','away',now64(3))");
        string Row(string project, string tenant, string type, string traffic, string time, string amount, string tag, string nullable) =>
            $"('{Guid.NewGuid()}','{project}','{tenant}','s','{type}','{traffic}','2090-01-01 {time}',{amount},7,'{tag}',{nullable})";
        string[] rows =
        [
            Row(Project, "t", "action", "normal", "00:00:00.000", "999", "x", "NULL"),
            Row(Project, "t", "action", "normal", "00:00:00.001", "10", "x", "NULL"),
            Row(Project, "t", "action", "bot", "00:00:00.500", "999", "x", "''"),
            Row(Project, "t", "action", "normal", "00:00:00.999", "NULL", "x", "''"),
            Row(Project, "t", "action", "normal", "00:00:01.000", "10", "x", "NULL"),
            Row(Project, "t", "action", "normal", "01:00:00.000", "20", "x", "NULL"),
            Row(Project, "t", "other", "normal", "01:00:00.001", "100", "y", "''"),
            Row(Project, "t", "action", "normal", "01:01:00.000", "NULL", "y", "NULL"),
            Row(Project, "t", "action", "normal", "02:00:00.000", "30", "x", "''"),
            Row(Project, "other", "action", "normal", "02:50:00.000", "50", "y", "NULL"),
            Row(Project + "_other", "t", "action", "normal", "02:50:00.000", "60", "y", "NULL"),
            Row(Project, "t", "action", "normal", "03:00:00.000", "40", "y", "''"),
            Row(Project, "t", "action", "normal", "03:00:00.001", "999", "y", "NULL")
        ];
        await ClickHouseTestSupport.ExecuteAsync(Insert(string.Join(',', rows)));
    }

    private string Insert(string rows, string settings = "") =>
        $"INSERT INTO nealytics_core.global_events (event_id,project_id,tenant_id,session_id,event_type,traffic_class,timestamp,{Amount},{Integer},{Tag},{Nullable}) {settings} VALUES {rows}";

    public async Task DisposeAsync()
    {
        await ClickHouseTestSupport.DeleteProjectsAsync(Project, Project + "_other");
        await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.tenant_attributes DELETE WHERE project_id = '{Project}' SETTINGS mutations_sync = 2");
    }

    private async Task<JsonDocument> Read(string path, string from, string to, string execution = "auto", string extra = "", bool tenantSet = false)
    {
        List<Claim> claims = [new("project_id", Project)];
        claims.Add(tenantSet ? new("tenant_set", "cohort:group") : new("tenant_id", "t"));
        var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("this-is-a-test-jwt-key-at-least-32-bytes-long!!")), SecurityAlgorithms.HmacSha256));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/analytics/" + path
            + "?from=" + Uri.EscapeDataString("2090-01-01T" + from + "Z")
            + "&to=" + Uri.EscapeDataString("2090-01-01T" + to + "Z") + "&execution=" + execution + extra);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        using var response = await _client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, response.StatusCode + ": " + body);
        return JsonDocument.Parse(body);
    }

    private static string Values(JsonDocument document) =>
        document.RootElement.TryGetProperty("rows", out var rows)
            ? document.RootElement.GetProperty("fields").GetRawText() + rows.GetRawText()
            : document.RootElement.GetProperty("value").GetRawText();

    [Theory]
    [InlineData("scalar", "00:00:00.000", "03:00:00.000", "rollup")]
    [InlineData("scalar", "00:00:00.001", "03:00:00.001", "hybrid")]
    [InlineData("grouped", "00:00:00.001", "03:00:00.001", "hybrid")]
    [InlineData("hourly", "00:00:00.001", "03:00:00.001", "hybrid")]
    [InlineData("daily", "00:00:00.001", "03:00:00.001", "hybrid")]
    [InlineData("nullable", "00:00:00.001", "03:00:00.001", "raw")]
    [InlineData("computed", "00:00:00.001", "03:00:00.001", "raw")]
    public async Task ReportsMatchRawAcrossShapesFormulaFinalizationNullsAndMillisecondEdges(string name, string from, string to, string source)
    {
        using var raw = await Read("reports/" + name, from, to, "raw");
        using var automatic = await Read("reports/" + name, from, to);
        Assert.Equal(source, automatic.RootElement.GetProperty("source").GetString());
        Assert.Equal("raw", raw.RootElement.GetProperty("source").GetString());
        Assert.Equal(Values(raw), Values(automatic));
        Assert.Equal("exact", automatic.RootElement.GetProperty("accuracy").GetString());
    }

    [Theory]
    [InlineData("events", "hybrid", "")]
    [InlineData("sum", "hybrid", "")]
    [InlineData("avg", "hybrid", "")]
    [InlineData("min", "hybrid", "")]
    [InlineData("max", "hybrid", "")]
    [InlineData("count", "hybrid", "")]
    [InlineData("ratio", "hybrid", "")]
    [InlineData("wide", "raw", "")]
    [InlineData("exact_quantile", "raw", "")]
    [InlineData("approx_quantile", "raw", "&accuracy=approximate")]
    public async Task ScalarMetricsKeepTheirAggregationAndAccuracyContracts(string metric, string source, string extra)
    {
        using var raw = await Read("metrics/" + metric, "00:00:00.001", "03:00:00.001", "raw", extra);
        using var automatic = await Read("metrics/" + metric, "00:00:00.001", "03:00:00.001", extra: extra);
        Assert.Equal(source, automatic.RootElement.GetProperty("source").GetString());
        Assert.Equal(Values(raw), Values(automatic));
        Assert.Equal(metric == "approx_quantile" ? "approximate" : "exact", automatic.RootElement.GetProperty("accuracy").GetString());
        if (metric == "avg")
        {
            Assert.Equal("22", automatic.RootElement.GetProperty("value").GetString());
        }
    }

    [Theory]
    [InlineData("", "hybrid")]
    [InlineData("&traffic=all", "hybrid")]
    [InlineData("&filter=tag", "hybrid")]
    [InlineData("&filter=measure", "raw")]
    public async Task FilteredReportsPreserveCurrentTenantMembership(string filter, string source)
    {
        filter = filter.Replace("filter=tag", "filter=" + Tag + ":x", StringComparison.Ordinal)
            .Replace("filter=measure", "filter=" + Amount + ">0", StringComparison.Ordinal);
        using var raw = await Read("reports/scalar", "00:00:00.001", "03:00:00.001", "raw", filter, true);
        using var automatic = await Read("reports/scalar", "00:00:00.001", "03:00:00.001", extra: filter, tenantSet: true);
        Assert.Equal(source, automatic.RootElement.GetProperty("source").GetString());
        Assert.Equal(Values(raw), Values(automatic));
        await ClickHouseTestSupport.ExecuteAsync($"INSERT INTO nealytics_core.tenant_attributes VALUES ('{Project}','cohort','other','group',now64(3) + INTERVAL 1 SECOND)");
        using var changedRaw = await Read("reports/scalar", "00:00:00.001", "03:00:00.001", "raw", filter, true);
        using var changed = await Read("reports/scalar", "00:00:00.001", "03:00:00.001", extra: filter, tenantSet: true);
        Assert.Equal(Values(changedRaw), Values(changed));
    }

    [Fact]
    public async Task LateAndDeduplicatedReplayRowsPreserveStateResultsAndReadinessFencesForceRaw()
    {
        var writer = _factory.Services.GetRequiredService<ClickHouseBatchWriter>();
        var unit = new TelemetryDeliveryUnit
        {
            Schema = writer.SchemaFingerprint,
            SourceId = await writer.GetSourceIdAsync(CancellationToken.None),
            Items = [new()
            {
                ProjectId = Project, TenantId = "t", SessionId = "s", EventType = "action",
                Timestamp = new DateTime(2090, 1, 1, 1, 30, 0, DateTimeKind.Utc),
                Dimensions = new() { [Tag] = "x" }, Measures = new() { [Amount] = "5", [Integer] = "7" }
            }]
        };
        await ((IDeliveryBatchWriter)writer).WriteDeliveryAsync(unit, CancellationToken.None);
        await ((IDeliveryBatchWriter)writer).WriteDeliveryAsync(unit, CancellationToken.None);
        using var raw = await Read("metrics/sum", "00:00:00.001", "03:00:00.001", "raw");
        using var automatic = await Read("metrics/sum", "00:00:00.001", "03:00:00.001");
        Assert.Equal("115", raw.RootElement.GetProperty("value").GetString());
        Assert.Equal("115", automatic.RootElement.GetProperty("value").GetString());
        Assert.Equal(Values(raw), Values(automatic));
        var registry = _factory.Services.GetRequiredService<RollupRegistry>();
        var rollup = Assert.Single(registry.Declared);
        registry.MarkUnroutable(rollup, "ambiguous replay identity");
        try
        {
            using var fenced = await Read("metrics/sum", "00:00:00.001", "03:00:00.001");
            Assert.Equal("raw", fenced.RootElement.GetProperty("source").GetString());
            Assert.Equal(Values(raw), Values(fenced));
        }
        finally
        {
            registry.MarkRoutable(rollup);
        }
    }

    [Fact]
    public async Task EmptySelectionsPreserveZeroCountsAndSumsAndNullOtherAggregates()
    {
        using var raw = await Read("reports/scalar", "00:00:00.001", "03:00:00.001", "raw", "&filter=event_type:absent");
        using var automatic = await Read("reports/scalar", "00:00:00.001", "03:00:00.001", extra: "&filter=event_type:absent");
        Assert.Equal("hybrid", automatic.RootElement.GetProperty("source").GetString());
        Assert.Equal(Values(raw), Values(automatic));
        var fields = automatic.RootElement.GetProperty("fields");
        Assert.Equal("0", fields[0].GetProperty("value").GetString());
        Assert.Equal("0", fields[2].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, fields[3].GetProperty("value").ValueKind);
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];
        internal string Amount => "c8_amount_" + _suffix;
        internal string Integer => "c8_int_" + _suffix;
        internal string Tag => "c8_tag_" + _suffix;
        internal string Nullable => "c8_nullable_" + _suffix;
        private string Rollup => "c8_hour_" + _suffix;
        internal Factory Host { get; private set; } = null!;
        internal HttpClient Client { get; private set; } = null!;

        public Task InitializeAsync()
        {
            var options = new TelemetryEngineOptions
            {
                EnableAnalyticsRollupPlanning = true,
                AnalyticsRollupSourceIsAppendOnly = true,
                Dimensions = [new() { Name = Tag, Type = "LowCardinality" }, new() { Name = Nullable }],
                Measures = [new() { Name = Amount, Type = "Float64", Aggregations = "sum,avg,min,max,count,p50,p75,p90,p95,p99" }, new() { Name = Integer, Type = "Int64" }],
                TenantAttributes = [new() { Name = "cohort" }],
                NamedMetrics = [new() { Name = "events" }, new() { Name = "actions", EventType = "action" }],
                ComputedDimensions = [new() { Name = "normalized", Source = Tag, Operation = "lower" }],
                Rollups = [new() { Name = Rollup, Grain = "hour", Dimensions = Tag + "," + Nullable, Measures = $"{Amount}:sum,{Amount}:avg,{Amount}:min,{Amount}:max,{Amount}:count" }]
            };
            foreach (string aggregation in new[] { "sum", "avg", "min", "max", "count" })
            {
                options.NamedMetrics.Add(new() { Name = aggregation, Measure = Amount, Aggregation = aggregation, EventType = "action" });
            }
            options.NamedMetrics.Add(new() { Name = "wide", Measure = Integer, Aggregation = "sum" });
            options.NamedMetrics.Add(new() { Name = "exact_quantile", Measure = Amount, Aggregation = "p95" });
            options.NamedMetrics.Add(new() { Name = "approx_quantile", Measure = Amount, Aggregation = "p95", Accuracy = "approximate" });
            options.NamedMetrics.Add(new()
            {
                Name = "ratio", Aggregation = "formula", Expression = new()
                {
                    Operator = "divide", Arguments = [new() { Operator = "metric", Metric = "actions" }, new() { Operator = "metric", Metric = "events" }]
                }
            });
            foreach (string name in new[] { "scalar", "grouped", "hourly", "daily", "nullable", "computed" })
            {
                options.SavedReports.Add(new()
                {
                    Name = name,
                    Interval = name == "hourly" ? "hour" : name == "daily" ? "day" : "",
                    GroupBy = name == "nullable" ? [Nullable] : name == "computed" ? ["normalized"] : name is "grouped" or "hourly" or "daily" ? [Tag, "event_type"] : [],
                    Fields = new[] { "events", "actions", "sum", "avg", "min", "max", "count", "ratio" }
                        .Select(metric => new SavedReportFieldOptions { Name = metric, Metric = metric }).ToList()
                });
            }
            options.SavedReports.Add(new() { Name = "approx_report", Fields = [new() { Name = "q", Metric = "approx_quantile" }] });
            Host = new Factory(options);
            Client = Host.CreateClient();
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            Client?.Dispose();
            if (Host is not null)
            {
                await Host.DisposeAsync();
            }
            await ClickHouseTestSupport.ExecuteAsync($"DROP VIEW IF EXISTS nealytics_core.rollup_{Rollup}_mv");
            await ClickHouseTestSupport.ExecuteAsync($"DROP TABLE IF EXISTS nealytics_core.rollup_{Rollup}");
            await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.global_events DROP COLUMN IF EXISTS {Amount}, DROP COLUMN IF EXISTS {Integer}, DROP COLUMN IF EXISTS {Tag}, DROP COLUMN IF EXISTS {Nullable}");
        }
    }

    internal sealed class Factory : TestWebApplicationFactory
    {
        private readonly string _schema;
        private readonly string? _previous;
        private readonly string _directory;

        public Factory(TelemetryEngineOptions options)
        {
            _directory = Environment.GetEnvironmentVariable("TelemetryEngine__WriteAheadLogDirectory")!;
            _schema = _directory + ".json";
            File.WriteAllText(_schema, JsonSerializer.Serialize(new Dictionary<string, TelemetryEngineOptions> { ["TelemetryEngine"] = options }));
            _previous = Environment.GetEnvironmentVariable("TelemetryEngine__SchemaFile");
            Environment.SetEnvironmentVariable("TelemetryEngine__SchemaFile", _schema);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(TelemetryBatchProcessor)).ToArray())
                {
                    services.Remove(descriptor);
                }
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Environment.SetEnvironmentVariable("TelemetryEngine__SchemaFile", _previous);
            File.Delete(_schema);
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
    }
}

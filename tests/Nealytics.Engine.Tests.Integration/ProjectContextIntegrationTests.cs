using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Microsoft.IdentityModel.Tokens;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class ProjectContextIntegrationTests
{
    [Fact]
    public async Task PinnedProjectsKeepTheirOwnValidationDefinitionsAndStoredResultsAcrossBothIngestPaths()
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string alpha = "c7a_" + suffix;
        string beta = "c7b_" + suffix;
        string left = "c7_left_" + suffix;
        string right = "c7_right_" + suffix;
        string measure = "c7_amount_" + suffix;
        var options = new TelemetryEngineOptions
        {
            IngestionKeys = [new() { Key = "alpha-key", Scope = "server", ProjectId = alpha }, new() { Key = "beta-key", Scope = "server", ProjectId = beta }],
            ProjectSchemas =
            [
                new()
                {
                    ProjectId = alpha,
                    Dimensions = [new() { Name = left }],
                    Measures = [new() { Name = measure, Type = "Int64", Minimum = 0, Maximum = 10 }],
                    NamedMetrics = [new() { Name = "activity", EventType = "first" }, new() { Name = "total", Aggregation = "sum", Measure = measure }],
                    ComputedDimensions = [new() { Name = "normalized", Source = left, Operation = "lower" }],
                    SavedReports = [new() { Name = "overview", GroupBy = ["normalized"], Fields = [new() { Name = "count", Metric = "activity" }, new() { Name = "sum", Metric = "total" }] }]
                },
                new()
                {
                    ProjectId = beta,
                    Dimensions = [new() { Name = right }],
                    Measures = [new() { Name = measure, Type = "Int64", Minimum = -10, Maximum = 100 }],
                    NamedMetrics = [new() { Name = "activity", EventType = "second" }, new() { Name = "total", Aggregation = "sum", Measure = measure }]
                }
            ]
        };
        try
        {
            await using var factory = new Factory(options);
            using var client = factory.CreateClient();
            string Payload(string project, string type, int amount) => JsonSerializer.Serialize(new
            {
                projectId = project, tenantId = "t", sessionId = "s", eventType = type, timestamp = "2090-01-01T00:00:00Z",
                dimensions = new Dictionary<string, string> { [left] = "EAST", [right] = "WEST" },
                measures = new Dictionary<string, string> { [measure] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            });
            // Server clocks reject far-future input, so use a fixed recent UTC instant for ingestion and reads.
            string now = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            string Recent(string project, string type, int amount) => Payload(project, type, amount).Replace("2090-01-01T00:00:00Z", now, StringComparison.Ordinal);
            using var track = await client.PostAsync("/api/v1/telemetry/track?k=alpha-key", new StringContent(Recent(alpha, "first", -1), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, track.StatusCode);
            Assert.Equal(right, Assert.Single(track.Headers.GetValues("X-Nealytics-Dropped")));
            using var beacon = await client.PostAsync("/api/v1/telemetry/beacon?k=beta-key", new StringContent("[" + Recent(beta, "second", -1) + "," + Recent(alpha, "first", 5) + "]", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NoContent, beacon.StatusCode);
            var broker = factory.Services.GetRequiredService<TelemetryChannelBroker>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            GlobalTelemetryPayload a = await broker.Reader.ReadAsync(timeout.Token);
            GlobalTelemetryPayload b = await broker.Reader.ReadAsync(timeout.Token);
            Assert.Equal(alpha, a.ProjectId);
            Assert.Equal(beta, b.ProjectId);
            Assert.Equal("", a.Measures![measure]);
            Assert.Equal("-1", b.Measures![measure]);
            Assert.Equal(left, Assert.Single(a.Dimensions!).Key);
            Assert.Equal(right, Assert.Single(b.Dimensions!).Key);
            var writer = factory.Services.GetRequiredService<ClickHouseBatchWriter>();
            await writer.WriteAsync([a, b], 2, timeout.Token);
            string range = "?from=" + Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-1).ToString("O"))
                + "&to=" + Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(1).ToString("O"));
            async Task<JsonDocument> Read(string project, string path)
            {
                var token = new JwtSecurityToken(claims: [new Claim("project_id", project), new Claim("tenant_id", "t")],
                    expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes("this-is-a-test-jwt-key-at-least-32-bytes-long!!")), SecurityAlgorithms.HmacSha256));
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            }
            using var alphaMetric = await Read(alpha, "/api/v1/analytics/metrics/activity" + range);
            using var betaMetric = await Read(beta, "/api/v1/analytics/metrics/activity" + range);
            Assert.Equal("1", alphaMetric.RootElement.GetProperty("value").GetString());
            Assert.Equal("1", betaMetric.RootElement.GetProperty("value").GetString());
            using var alphaTotal = await Read(alpha, "/api/v1/analytics/metrics/total" + range);
            using var betaTotal = await Read(beta, "/api/v1/analytics/metrics/total" + range);
            Assert.Equal("0", alphaTotal.RootElement.GetProperty("value").GetString());
            Assert.Equal("-1", betaTotal.RootElement.GetProperty("value").GetString());
            using var report = await Read(alpha, "/api/v1/analytics/reports/overview" + range);
            var row = Assert.Single(report.RootElement.GetProperty("rows").EnumerateArray());
            Assert.Equal("east", Assert.Single(row.GetProperty("groups").EnumerateArray()).GetProperty("value").GetString());
            Assert.Equal("1", row.GetProperty("fields")[0].GetProperty("value").GetString());
            using var schema = await Read(beta, "/api/v1/schema");
            Assert.Equal(beta, schema.RootElement.GetProperty("projectId").GetString());
            Assert.Equal(right, Assert.Single(schema.RootElement.GetProperty("dimensions").EnumerateArray()).GetProperty("name").GetString());
            Assert.Equal(1, await ClickHouseTestSupport.CountAsync(alpha));
            Assert.Equal(1, await ClickHouseTestSupport.CountAsync(beta));
        }
        finally
        {
            await ClickHouseTestSupport.DeleteProjectsAsync(alpha, beta);
            await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.global_events DROP COLUMN IF EXISTS {left}, DROP COLUMN IF EXISTS {right}, DROP COLUMN IF EXISTS {measure}");
        }
    }

    private sealed class Factory : TestWebApplicationFactory
    {
        private readonly string _schema;
        private readonly string? _previous;
        private readonly string _directory;

        public Factory(TelemetryEngineOptions options)
        {
            _directory = Environment.GetEnvironmentVariable("TelemetryEngine__WriteAheadLogDirectory")!;
            _schema = Path.Combine(_directory + ".json");
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

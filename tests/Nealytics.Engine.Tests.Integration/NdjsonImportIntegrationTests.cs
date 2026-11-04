using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class NdjsonImportIntegrationTests
{
    [Theory]
    [InlineData("managed", false)]
    [InlineData("custom", false)]
    [InlineData("managed", true)]
    [InlineData("custom", true)]
    public async Task AFileImportsItsDurablePrefixIntoThePinnedSchemaAndBecomesQueryable(string path, bool mapped)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string project = "e2_" + suffix;
        string measure = "e2_amount_" + suffix;
        await using var factory = new Factory(project, measure, path, mapped);
        using var client = factory.CreateClient();
        string Event(int value) => mapped ? JsonSerializer.Serialize(new
        {
            source = new { id = Guid.NewGuid(), time = DateTime.UtcNow.AddHours(-1), tenant = "t", amount = value }
        }) : JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(), timestamp = DateTime.UtcNow.AddHours(-1), projectId = project,
            tenantId = "t", eventType = "action", measures = new Dictionary<string, string> { [measure] = value.ToString() }
        });
        string prefix = Event(2) + "\r\n" + Event(4) + "\n";
        try
        {
            using var response = await client.PostAsync("/api/v1/telemetry/import/ndjson?k=import-key",
                new StringContent(prefix + "{\n" + Event(100), Encoding.UTF8, "application/x-ndjson"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(2, receipt.RootElement.GetProperty("acceptedRecords").GetInt32());
            Assert.Equal(3, receipt.RootElement.GetProperty("nextLine").GetInt32());
            Assert.Equal(Encoding.UTF8.GetByteCount(prefix), receipt.RootElement.GetProperty("nextByteOffset").GetInt64());
            Assert.Equal("invalid_json", receipt.RootElement.GetProperty("error").GetString());
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
            while (await ClickHouseTestSupport.CountAsync(project) != 2) await Task.Delay(25, deadline.Token);
            var token = new JwtSecurityToken(claims: [new Claim("project_id", project), new Claim("tenant_id", "t")],
                expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes("this-is-a-test-jwt-key-at-least-32-bytes-long!!")), SecurityAlgorithms.HmacSha256));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/analytics/metrics/total");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            using var query = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, query.StatusCode);
            using var result = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
            Assert.Equal("6", result.RootElement.GetProperty("value").GetString());
            Assert.Equal("raw", result.RootElement.GetProperty("source").GetString());
        }
        finally
        {
            await factory.DisposeAsync();
            await ClickHouseTestSupport.DeleteProjectsAsync(project);
            await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.global_events DROP COLUMN IF EXISTS {measure}");
        }
    }

    internal sealed class Factory : TestWebApplicationFactory
    {
        private readonly string _schema;
        private readonly string? _previous;
        private readonly string _directory;
        private readonly bool _preserveWal;
        private bool _disposed;
        internal Factory(string project, string measure, string path, bool mapped, bool jobs = false,
            S3SourceOptions? source = null, string? directory = null, bool preserveWal = false)
        {
            _directory = directory ?? Environment.GetEnvironmentVariable("TelemetryEngine__WriteAheadLogDirectory")!;
            _preserveWal = preserveWal;
            if (directory is not null) Environment.SetEnvironmentVariable("TelemetryEngine__WriteAheadLogDirectory", directory);
            _schema = _directory + ".json";
            var options = new TelemetryEngineOptions
            {
                EnableNdjsonImport = true, EnableImportJobs = jobs, IngestionExecutionPath = path,
                IngestionKeys = [new() { Key = "import-key", Scope = "server", ProjectId = project }],
                ProjectSchemas = [new()
                {
                    ProjectId = project,
                    Measures = [new() { Name = measure, Type = "Int64" }],
                    NamedMetrics = [new() { Name = "total", Aggregation = "sum", Measure = measure }]
                }]
            };
            if (source is not null)
            {
                options.S3Sources = [source];
                options.EnableImportJobs = true;
                options.ImportMaxRecords = 1;
            }
            if (mapped)
            {
                options.NdjsonSourceMappings[project] = new()
                {
                    Fields = new()
                    {
                        ["eventId"] = new() { Path = ["source", "id"] },
                        ["timestamp"] = new() { Path = ["source", "time"] },
                        ["projectId"] = new() { Default = project },
                        ["tenantId"] = new() { Path = ["source", "tenant"] },
                        ["eventType"] = new() { Default = "action" },
                        ["measures." + measure] = new() { Path = ["source", "amount"] }
                    }
                };
            }
            File.WriteAllText(_schema, JsonSerializer.Serialize(new Dictionary<string, TelemetryEngineOptions> { ["TelemetryEngine"] = options }));
            _previous = Environment.GetEnvironmentVariable("TelemetryEngine__SchemaFile");
            Environment.SetEnvironmentVariable("TelemetryEngine__SchemaFile", _schema);
            UseKestrel(0);
        }
        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { await base.DisposeAsync(); }
            finally
            {
                Environment.SetEnvironmentVariable("TelemetryEngine__SchemaFile", _previous);
                File.Delete(_schema);
                if (!_preserveWal && Directory.Exists(_directory)) Directory.Delete(_directory, true);
            }
        }
    }
}

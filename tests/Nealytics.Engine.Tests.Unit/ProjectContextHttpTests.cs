using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Features.BatchProcessor;
using Microsoft.Extensions.Hosting;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Project context hosts")]
public sealed class ProjectContextHttpTests
{
    public sealed class Factory : NoDatabaseWebFactory
    {
        private readonly string _schema = Path.Combine(Path.GetTempPath(), "nealytics-project-schema-" + Guid.NewGuid().ToString("N") + ".json");
        private readonly string? _previous;

        public Factory(bool legacy = false, Action<TelemetryEngineOptions>? customize = null)
        {
            var options = ProjectContextTests.Options();
            options.AllowLegacyProjectSchema = legacy;
            options.ReadScopeClaim = "scope";
            options.IngestionKeys =
            [
                new() { Key = "alpha-key", Scope = "server", ProjectId = "alpha" },
                new() { Key = "beta-key", Scope = "server", ProjectId = "beta" },
                new() { Key = "alpha-public", Scope = "public", ProjectId = "alpha" },
                new() { Key = "unknown-key", Scope = "public", ProjectId = "unknown" }
            ];
            customize?.Invoke(options);
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
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.Events.OnForbidden = context =>
                {
                    context.Response.Headers["X-Test-Forbidden"] = "handled";
                    return Task.CompletedTask;
                });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Environment.SetEnvironmentVariable("TelemetryEngine__SchemaFile", _previous);
            File.Delete(_schema);
        }
    }

    internal static HttpRequestMessage Read(string path, string? project, string? tenant = "t", string scope = "*")
    {
        List<Claim> claims = [new("scope", scope)];
        if (project is not null)
        {
            claims.Add(new("project_id", project));
        }
        if (tenant is not null)
        {
            claims.Add(new("tenant_id", tenant));
        }
        var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(NoDatabaseWebFactory.JwtKey)), SecurityAlgorithms.HmacSha256));
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }

    [Fact]
    public async Task ConcurrentDiscoveryUsesTheAuthenticatedProjectAndOmitsOtherDefinitions()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        async Task Check(string project, string own, string hidden, string type)
        {
            using var request = Read("/api/v1/schema?projectId=" + (project == "alpha" ? "beta" : "alpha"), project);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var schema = document.RootElement;
            Assert.Equal(project, schema.GetProperty("projectId").GetString());
            string[] columns = schema.GetProperty("dimensions").EnumerateArray().Select(column => column.GetProperty("name").GetString()!).ToArray();
            Assert.Contains(own, columns);
            Assert.DoesNotContain(hidden, columns);
            Assert.All(schema.GetProperty("dimensions").EnumerateArray(), dimension =>
            {
                Assert.True(dimension.GetProperty("groupable").GetBoolean());
                Assert.True(dimension.GetProperty("filterable").GetBoolean());
            });
            Assert.DoesNotContain("legacy", columns);
            Assert.Equal(type, Assert.Single(schema.GetProperty("namedMetrics").EnumerateArray()).GetProperty("eventType").GetString());
            Assert.DoesNotContain("legacy_count", await response.Content.ReadAsStringAsync());
            Assert.Equal(project == "alpha" ? 1 : 0, schema.GetProperty("computedDimensions").GetArrayLength());
            Assert.Equal(project == "alpha" ? 1 : 0, schema.GetProperty("savedReports").GetArrayLength());
            if (project == "alpha")
            {
                Assert.Equal("tenant.cohort", Assert.Single(schema.GetProperty("tenantAttributes").EnumerateArray()).GetString());
            }
            Assert.Equal(project + (project == "alpha" ? "_hour" : "_day"), Assert.Single(schema.GetProperty("rollups").EnumerateArray()).GetProperty("name").GetString());
        }
        await Task.WhenAll(Check("alpha", "region", "category", "first"), Check("beta", "category", "region", "second"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Alpha")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task MissingOrUnknownReadProjectsAreForbidden(string? project)
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = Read("/api/v1/schema", project);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("handled", Assert.Single(response.Headers.GetValues("X-Test-Forbidden")));
        Assert.Empty(await response.Content.ReadAsStringAsync());
        using var noCredentials = await client.GetAsync("/api/v1/schema");
        Assert.Equal(HttpStatusCode.Unauthorized, noCredentials.StatusCode);
    }

    [Fact]
    public async Task OversizedClaimsCannotFallThroughToTheBaselineSchema()
    {
        await using var factory = new Factory(true);
        using var client = factory.CreateClient();
        using var request = Read("/api/v1/schema", new string('x', 257));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("handled", Assert.Single(response.Headers.GetValues("X-Test-Forbidden")));
    }

    [Fact]
    public async Task OptInLegacyReadsExposeOnlyTheBaselineAndKeepScopeChecks()
    {
        await using var factory = new Factory(true);
        using var client = factory.CreateClient();
        using var request = Read("/api/v1/schema", "unconfigured", scope: "schema");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.TryGetProperty("projectId", out _));
        Assert.Equal("legacy", Assert.Single(document.RootElement.GetProperty("dimensions").EnumerateArray()).GetProperty("name").GetString());
        using var denied = Read("/api/v1/schema", "alpha", scope: "reports");
        using var forbidden = await client.SendAsync(denied);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Theory]
    [InlineData("alpha", "/api/v1/analytics/metrics/legacy_count", 404)]
    [InlineData("beta", "/api/v1/analytics/reports/overview", 404)]
    [InlineData("beta", "/api/v1/analytics/metrics/activity?filter=region:east", 400)]
    [InlineData("alpha", "/api/v1/analytics/metrics/activity?filter=category:x", 400)]
    [InlineData("alpha", "/api/v1/analytics/reports/overview?param.missing=x", 400)]
    public async Task QueryDefinitionsAndColumnsDoNotLeakAcrossProjects(string project, string path, int status)
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = Read(path, project);
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("alpha-key", "alpha", "region", "category")]
    [InlineData("beta-key", "beta", "category", "region")]
    public async Task PinnedIngestionUsesOnlyItsProjectSchema(string key, string project, string own, string hidden)
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        string json = JsonSerializer.Serialize(new
        {
            projectId = project, tenantId = "t", sessionId = "s", eventType = "e",
            dimensions = new Dictionary<string, string> { [own] = "v", [hidden] = "x" },
            measures = new Dictionary<string, string> { ["amount"] = "-1" }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telemetry/validate?k=" + key)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(hidden, Assert.Single(root.GetProperty("droppedDimensions").EnumerateArray()).GetString());
        Assert.Equal("v", root.GetProperty("storedDimensions").GetProperty(own).GetString());
        Assert.Equal(project == "alpha" ? 1 : 0, root.GetProperty("rejectedValues").GetArrayLength());
        Assert.DoesNotContain(hidden, root.GetProperty("columns").EnumerateArray().Select(item => item.GetString()));
    }

    [Theory]
    [InlineData("unit-key-2", "alpha", false, 200)]
    [InlineData("alpha-key", "beta", false, 200)]
    [InlineData("alpha-public", "alpha", true, 200)]
    [InlineData("unknown-key", "unknown", false, 403)]
    [InlineData("wrong", "alpha", false, 401)]
    [InlineData("", "alpha", false, 401)]
    public async Task UnknownUnpinnedCrossProjectAndServerOnlyWritesAreRejected(string key, string project, bool measure, int status)
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telemetry/validate?k=" + key)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                projectId = project, tenantId = "t", sessionId = "s", eventType = "e",
                measures = measure ? new Dictionary<string, string> { ["amount"] = "10" } : null
            }), Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 200)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("accepted").GetBoolean());
            Assert.Contains(measure ? "only a server key" : "not permitted", body.RootElement.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task HeaderCredentialsTakePrecedenceOverQueryKeysAndJwtForWrites()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = Read("/api/v1/telemetry/validate?k=beta-key", "beta");
        request.Method = HttpMethod.Post;
        request.Headers.Add("X-Project-Key", "alpha-key");
        request.Content = new StringContent("""{"projectId":"alpha","tenantId":"t","sessionId":"s","eventType":"e","dimensions":{"region":"east"}}""", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal("east", body.RootElement.GetProperty("storedDimensions").GetProperty("region").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public async Task BeaconUsesItsQueryCredentialEvenWhenAConflictingHeaderIsPresent(string suffix)
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telemetry/beacon" + suffix + "?k=beta-key");
        request.Headers.Add("X-Project-Key", "alpha-key");
        request.Content = new StringContent("""[{"projectId":"beta","tenantId":"t","sessionId":"s","eventType":"e","dimensions":{"category":"west"},"measures":{"amount":"-1"}}]""", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(factory.Services.GetRequiredService<TelemetryChannelBroker>().Reader.TryRead(out var payload));
        Assert.Equal("beta", payload!.ProjectId);
        Assert.Equal("west", payload.Dimensions!["category"]);
        Assert.Equal("-1", payload.Measures!["amount"]);
    }
}

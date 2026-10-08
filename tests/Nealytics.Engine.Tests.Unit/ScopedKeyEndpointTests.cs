using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class ScopedKeysWebFactory : NoDatabaseWebFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            TelemetryEngineOptions declared = new()
            {
                Measures = [new MeasureOptions { Name = "amount", Type = "Decimal", Aggregations = "sum", ServerOnly = true }],
                TenantAttributes = [new TenantAttributeOptions { Name = "parent" }],
            };
            DimensionRegistry dimensions = new(declared);
            services.AddSingleton(new MeasureRegistry(declared, dimensions));
            services.AddSingleton(new TenantAttributeRegistry(declared));

            services.Configure<TelemetryEngineOptions>(options =>
            {
                options.IngestionKeys =
                [
                    new IngestionKeyOptions { Key = "scoped-public", Scope = "public" },
                    new IngestionKeyOptions { Key = "scoped-server", Scope = "server", ProjectId = "p" },
                ];
                options.ServerEventTypes = "sale.*";
                options.ReadScopeClaim = "scope";
            });
        });
    }
}

public class ScopedKeyEndpointTests : IClassFixture<ScopedKeysWebFactory>
{
    private readonly HttpClient _client;

    public ScopedKeyEndpointTests(ScopedKeysWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string key, string json)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Project-Key", key);
        return await _client.SendAsync(request);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : string.Empty;

    [Fact]
    public async Task APublicKey_CannotSendAServerEvent()
    {
        HttpResponseMessage response = await PostAsync(
            "/api/v1/telemetry/track", "scoped-public",
            """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"sale.completed"}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Header(response, "X-Nealytics-Rejected").Should().Be("event_type_not_permitted_for_key");
    }

    [Fact]
    public async Task APublicKey_CannotSendAServerOnlyMeasure()
    {
        HttpResponseMessage response = await PostAsync(
            "/api/v1/telemetry/track", "scoped-public",
            """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"tip","measures":{"amount":"5"}}""");

        Header(response, "X-Nealytics-Rejected").Should().Be("server_only_field");
    }

    [Fact]
    public async Task APublicKeysBackdatedEvent_IsAcceptedAtReceiptTime()
    {
        HttpResponseMessage response = await PostAsync(
            "/api/v1/telemetry/track", "scoped-public",
            """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"view","timestamp":"2020-01-01T00:00:00Z"}""");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        Header(response, "X-Nealytics-Adjusted").Should().Be("timestamp");
    }

    [Fact]
    public async Task AServerKey_SendsASessionlessServerEvent()
    {
        HttpResponseMessage response = await PostAsync(
            "/api/v1/telemetry/track", "scoped-server",
            """{"projectId":"p","tenantId":"t","eventType":"sale.completed","measures":{"amount":"12.5"},"timestamp":"2026-01-01T00:00:00Z"}""");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        Header(response, "X-Nealytics-Adjusted").Should().BeEmpty();
    }

    [Fact]
    public async Task TheDryRun_ExplainsAScopeRefusal()
    {
        HttpResponseMessage response = await PostAsync(
            "/api/v1/telemetry/validate", "scoped-public",
            """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"sale.completed"}""");

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"accepted\":false").And.Contain("ServerEventTypes");
    }

    [Fact]
    public async Task TenantAttributes_AreWritableOnlyWithAServerKey()
    {
        const string body = """{"projectId":"p","tenants":[{"tenantId":"v1","attributes":{"parent":"org-1"}}]}""";

        (await PostAsync("/api/v1/tenants/attributes", "scoped-public", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync("/api/v1/tenants/attributes", "unit-key-2", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync("/api/v1/tenants/attributes", "nope", body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantAttributes_RefuseAnUndeclaredAttribute()
    {
        HttpResponseMessage response = await PostAsync(
            "/api/v1/tenants/attributes", "scoped-server",
            """{"projectId":"p","tenants":[{"tenantId":"v1","attributes":{"region":"west"}}]}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not a declared tenant attribute");
    }

    [Fact]
    public async Task TenantAttributes_RefuseAnotherProjectThanTheKeysPin()
    {
        HttpResponseMessage response = await PostAsync(
            "/api/v1/tenants/attributes", "scoped-server",
            """{"projectId":"q","tenants":[{"tenantId":"v1","attributes":{"parent":"org-1"}}]}""");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static string Jwt(params Claim[] extra)
    {
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(NoDatabaseWebFactory.JwtKey));
        JwtSecurityToken token = new(
            claims: [new Claim("project_id", "p"), new Claim("tenant_id", "t"), .. extra],
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<HttpStatusCode> ReadAsync(string path, string token)
    {
        HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Add("Authorization", $"Bearer {token}");
        return (await _client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task WithReadScopesOn_ATokenWithoutTheScopeIsForbidden()
    {
        (await ReadAsync("/api/v1/analytics/breakdown?groupBy=event_type", Jwt())).Should().Be(HttpStatusCode.Forbidden);
        (await ReadAsync("/api/v1/analytics/breakdown?groupBy=event_type", Jwt(new Claim("scope", "timeline"))))
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WithReadScopesOn_TheGrantedEndpointIsReached()
    {
        (await ReadAsync("/api/v1/analytics/breakdown?groupBy=nope", Jwt(new Claim("scope", "breakdown schema"))))
            .Should().Be(HttpStatusCode.BadRequest, "the request got past the scope check to the request factory");
    }

    [Fact]
    public async Task ASetToken_IsForbiddenForAnUndeclaredAttribute()
    {
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(NoDatabaseWebFactory.JwtKey));
        JwtSecurityToken token = new(
            claims: [new Claim("project_id", "p"), new Claim("tenant_set", "region:west"), new Claim("scope", "*")],
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        (await ReadAsync("/api/v1/analytics/breakdown?groupBy=event_type", new JwtSecurityTokenHandler().WriteToken(token)))
            .Should().Be(HttpStatusCode.Forbidden);
    }
}

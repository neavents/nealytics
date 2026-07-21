using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

namespace Nealytics.Engine.Tests.Unit;

public sealed class NoDatabaseWebFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "unit-test-jwt-signing-key-at-least-32-bytes!!";

    public NoDatabaseWebFactory()
    {
        Environment.SetEnvironmentVariable("TelemetryEngine__JwtSymmetricKey", JwtKey);
        Environment.SetEnvironmentVariable("TelemetryEngine__AllowedProjectKeys", "unit-key-1,unit-key-2");
        Environment.SetEnvironmentVariable("TelemetryEngine__ClickHouseConnectionString",
            "Host=127.0.0.1;Port=9;Database=nealytics_core;User=default;Password=;");
        Environment.SetEnvironmentVariable("TelemetryEngine__WriteAheadLogDirectory",
            Path.Combine(Path.GetTempPath(), $"nealytics_boot_{Guid.NewGuid():N}"));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}

public class EndpointBootTests : IClassFixture<NoDatabaseWebFactory>
{
    private readonly HttpClient _client;

    public EndpointBootTests(NoDatabaseWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static string Jwt(string? projectId, string? tenantId)
    {
        SymmetricSecurityKey key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(NoDatabaseWebFactory.JwtKey));
        SigningCredentials creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        List<Claim> claims = new List<Claim>();
        if (projectId is not null) claims.Add(new Claim("project_id", projectId));
        if (tenantId is not null) claims.Add(new Claim("tenant_id", tenantId));
        JwtSecurityToken token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url, string? projectId, string? tenantId)
    {
        HttpRequestMessage req = new HttpRequestMessage(method, url);
        req.Headers.Add("Authorization", $"Bearer {Jwt(projectId, tenantId)}");
        return req;
    }

    [Fact]
    public async Task Health_IsLiveness_Returns200_EvenWhenClickHouseUnreachable()
    {
        HttpResponseMessage response = await _client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_WhenClickHouseUnreachable_Returns503()
    {
        HttpResponseMessage response = await _client.GetAsync("/ready");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Track_NoKey_Returns401()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/v1/telemetry/track",
            new { projectId = "p", tenantId = "t", sessionId = "s", eventType = "e" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Track_BadKey_Returns401()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/v1/telemetry/track?k=wrong",
            new { projectId = "p", tenantId = "t", sessionId = "s", eventType = "e" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Track_EmptyRequiredFields_Returns400()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/v1/telemetry/track?k=unit-key-1",
            new { projectId = "", tenantId = "", sessionId = "", eventType = "" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Track_MalformedJson_Returns400()
    {
        StringContent content = new StringContent("not-json", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await _client.PostAsync("/api/v1/telemetry/track?k=unit-key-1", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Track_BodyTooLarge_Returns413()
    {
        string big = new string('z', 2_000_000);
        string json = "{\"projectId\":\"p\",\"tenantId\":\"t\",\"sessionId\":\"s\",\"eventType\":\"e\",\"metadataJson\":\"" + big + "\"}";
        StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await _client.PostAsync("/api/v1/telemetry/track?k=unit-key-1", content);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Beacon_NoKey_Returns401()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/v1/telemetry/beacon", new object[] { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Timeline_NoJwt_Returns401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v1/telemetry/timeline");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Timeline_JwtMissingClaims_Returns403()
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Get, "/api/v1/telemetry/timeline", projectId: null, tenantId: null));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Timeline_ClaimTooLong_Returns400()
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Get, "/api/v1/telemetry/timeline", new string('x', 300), "t"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TimeSeries_InvalidInterval_Returns400()
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Get, "/api/v1/analytics/timeseries?interval=fortnight", "p", "t"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TimeSeries_FromAfterTo_Returns400()
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Get,
                "/api/v1/analytics/timeseries?from=2030-01-01T00:00:00Z&to=2020-01-01T00:00:00Z", "p", "t"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sessions_FromAfterTo_Returns400()
    {
        HttpResponseMessage response = await _client.SendAsync(
            Authorized(HttpMethod.Get,
                "/api/v1/analytics/sessions?from=2030-01-01T00:00:00Z&to=2020-01-01T00:00:00Z", "p", "t"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sessions_NoJwt_Returns401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v1/analytics/sessions");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

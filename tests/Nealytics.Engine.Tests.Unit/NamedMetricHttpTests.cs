using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class NamedMetricHttpTests : IClassFixture<NamedMetricHttpTests.Factory>
{
    private readonly HttpClient _client;

    public NamedMetricHttpTests(Factory factory) => _client = factory.CreateClient();

    public sealed class Factory : NoDatabaseWebFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var declared = NamedMetricRegistryTests.Options();
                var dimensions = new DimensionRegistry(declared);
                services.AddSingleton(new NamedMetricRegistry(declared, new MeasureRegistry(declared, dimensions)));
                services.AddSingleton(new TenantAttributeRegistry(new TelemetryEngineOptions
                {
                    TenantAttributes = [new() { Name = "cohort" }]
                }));
                services.PostConfigure<TelemetryEngineOptions>(options => options.ReadScopeClaim = "scope");
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    options.Events.OnForbidden = context =>
                    {
                        context.Response.Headers["X-Test-Forbidden"] = "handled";
                        return Task.CompletedTask;
                    });
            });
        }
    }

    [Fact]
    public async Task MissingCredentialsAreRejected()
    {
        using var response = await _client.GetAsync("/api/v1/analytics/metrics/activity");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("schema", "p", "t", "missing", "", 403)]
    [InlineData("", "p", "t", "missing", "", 403)]
    [InlineData("metrics", "p", "t", "missing", "", 404)]
    [InlineData("*", "p", "t", "missing", "", 404)]
    [InlineData("metrics", null, "t", "activity", "", 403)]
    [InlineData("metrics", "p", null, "activity", "", 403)]
    [InlineData("metrics", "p", "t", "activity", "?from=invalid", 400)]
    [InlineData("metrics", "p", "t", "activity", "?traffic=invalid", 400)]
    [InlineData("metrics", "p", "t", "activity", "?filter=missing:x", 400)]
    public async Task ScopeIdentityAndInputFailuresReturnTheirOwnStatusBeforeStorage(
        string scope, string? project, string? tenant, string name, string suffix, int status)
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
        using var request = Request(name + suffix, claims);
        using var response = await _client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 403)
        {
            Assert.Empty(await response.Content.ReadAsStringAsync());
            Assert.Equal("handled", Assert.Single(response.Headers.GetValues("X-Test-Forbidden")));
        }
        if (status == 404)
        {
            Assert.Equal("\"Unknown named metric.\"", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task InvalidTenantSetNarrowingKeepsTheIdentityDiagnostic()
    {
        using var request = Request("activity?tenant=" + new string('t', 257),
            [new("scope", "metrics"), new("project_id", "p"), new("tenant_set", "cohort:g")]);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("must not exceed 256 characters", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownEventSegmentHasItsOwnNotFoundDiagnostic()
    {
        using var request = Request("activity?segment=missing",
            [new("scope", "metrics"), new("project_id", "p"), new("tenant_id", "t")]);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("\"Unknown event segment.\"", await response.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Request(string path, List<Claim> claims)
    {
        var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(NoDatabaseWebFactory.JwtKey)),
                SecurityAlgorithms.HmacSha256));
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/analytics/metrics/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }
}

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

public sealed class SavedReportHttpTests : IClassFixture<SavedReportHttpTests.Factory>
{
    private readonly HttpClient _client;

    public SavedReportHttpTests(Factory factory) => _client = factory.CreateClient();

    public sealed class Factory : NoDatabaseWebFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var declared = SavedReportRegistryTests.Options();
                declared.SavedReports[0].Parameters = [new() { Name = "location", Column = "country" }];
                var dimensions = new DimensionRegistry(declared);
                var measures = new MeasureRegistry(declared, dimensions);
                var metrics = new NamedMetricRegistry(declared, measures);
                var segments = new EventSegmentRegistry(declared, new Nealytics.Engine.Infrastructure.Query.QueryColumns(dimensions), measures);
                services.AddSingleton(metrics);
                services.AddSingleton(segments);
                services.AddSingleton(new SavedReportRegistry(declared, metrics, segments));
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
        using var response = await _client.GetAsync("/api/v1/analytics/reports/activity");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("schema", "p", "t", "missing", "", 403)]
    [InlineData("metrics", "p", "t", "missing", "", 403)]
    [InlineData("", "p", "t", "missing", "", 403)]
    [InlineData("reports", "p", "t", "missing", "", 404)]
    [InlineData("*", "p", "t", "missing", "", 404)]
    [InlineData("reports", null, "t", "overview", "", 403)]
    [InlineData("reports", "p", null, "overview", "", 403)]
    [InlineData("reports", "p", "t", "overview", "?from=invalid", 400)]
    [InlineData("reports", "p", "t", "overview", "?groupBy=country", 400)]
    [InlineData("reports", "p", "t", "overview", "?to=invalid", 400)]
    [InlineData("reports", "p", "t", "overview", "?traffic=invalid", 400)]
    [InlineData("reports", "p", "t", "overview", "?filter=missing:x", 400)]
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
            Assert.Equal("\"Unknown saved report.\"", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task InvalidTenantSetNarrowingKeepsTheIdentityDiagnostic()
    {
        using var request = Request("overview?tenant=" + new string('t', 257),
            [new("scope", "reports"), new("project_id", "p"), new("tenant_set", "cohort:g")]);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("must not exceed 256 characters", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CallersCannotReplaceTheConfiguredSegment()
    {
        using var request = Request("overview?segment=missing",
            [new("scope", "reports"), new("project_id", "p"), new("tenant_id", "t")]);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("\"Only from, to, traffic, filter, tenant, execution and accuracy are supported report parameters.\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeclaredParameterValuesAreParsedBeforeRangeValidationAndNeverReachStorageOnFailure()
    {
        using var request = Request("overview?param.location=GB&from=invalid",
            [new("scope", "reports"), new("project_id", "p"), new("tenant_id", "t")]);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Use a valid UTC range", await response.Content.ReadAsStringAsync());
        using var duplicate = Request("overview?param.location=GB&param.location=US",
            [new("scope", "reports"), new("project_id", "p"), new("tenant_id", "t")]);
        using var rejected = await _client.SendAsync(duplicate);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("\"Report parameter 'location' requires exactly one value.\"", await rejected.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Request(string path, List<Claim> claims)
    {
        var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(NoDatabaseWebFactory.JwtKey)),
                SecurityAlgorithms.HmacSha256));
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/analytics/reports/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }

    [Fact]
    public async Task EndpointRegistrationRequiresTheReportsScopeOnANewHost()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        using var allowedRequest = Request("missing",
            [new("scope", "reports"), new("project_id", "p"), new("tenant_id", "t")]);
        using var allowed = await client.SendAsync(allowedRequest);
        Assert.Equal(HttpStatusCode.NotFound, allowed.StatusCode);
        using var deniedRequest = Request("missing",
            [new("scope", "schema"), new("project_id", "p"), new("tenant_id", "t")]);
        using var denied = await client.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}

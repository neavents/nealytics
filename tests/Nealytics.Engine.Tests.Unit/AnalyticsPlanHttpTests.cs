using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Project context hosts")]
public sealed class AnalyticsPlanHttpTests
{
    private static HttpRequestMessage Request(string path, string? project = "alpha", string? tenant = "t", string scope = "*", string? role = "admin")
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
        if (role is not null)
        {
            claims.Add(new("role", role));
        }
        var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(NoDatabaseWebFactory.JwtKey)), SecurityAlgorithms.HmacSha256));
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }

    private const string Range = "?from=2090-01-01T00:00:00.001Z&to=2090-01-01T03:00:00.001Z";

    [Fact]
    public async Task ExplainBindsDeclaredReportParametersWithoutExecutingStorage()
    {
        await using var factory = new ProjectContextHttpTests.Factory(customize: options =>
            options.ProjectSchemas[0].SavedReports[0].Parameters = [new() { Name = "region", Column = "region", Required = true }]);
        using var client = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        Assert.Equal("ExplainNamedMetric", Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/analytics/metrics/{name}/explain").Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName);
        Assert.Equal("ExplainSavedReport", Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/analytics/reports/{name}/explain").Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName);
        using var request = Request("/api/v1/analytics/reports/overview/explain" + Range + "&param.region=west&execution=raw");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("overview", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("raw", document.RootElement.GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("metrics/activity?execution=invalid")]
    [InlineData("metrics/activity?accuracy=invalid")]
    [InlineData("reports/overview?execution=invalid")]
    [InlineData("reports/overview?accuracy=invalid")]
    public async Task ExecutionEndpointsValidateTheirForwardedOptionsBeforeStorage(string path)
    {
        await using var factory = new ProjectContextHttpTests.Factory();
        using var client = factory.CreateClient();
        using var request = Request("/api/v1/analytics/" + path);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(path.Contains("execution", StringComparison.Ordinal) ? "Execution must be" : "Accuracy must be",
            await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("metrics/q")]
    [InlineData("reports/approx")]
    public async Task ExecutionEndpointsRequireApproximateOptInAndForwardItWhenPresent(string path)
    {
        await using var factory = new ProjectContextHttpTests.Factory(customize: options =>
        {
            options.ProjectSchemas[0].Measures[0].Aggregations = "sum,avg,p95";
            options.ProjectSchemas[0].NamedMetrics.Add(new() { Name = "q", Measure = "amount", Aggregation = "p95", Accuracy = "approximate" });
            options.ProjectSchemas[0].SavedReports.Add(new() { Name = "approx", GroupBy = ["unit"], Fields = [new() { Name = "q", Metric = "q" }] });
        });
        using var client = factory.CreateClient();
        string selection = path.StartsWith("metrics", StringComparison.Ordinal) ? "?filter=unit:u" : "?traffic=normal";
        using var denied = Request("/api/v1/analytics/" + path + selection);
        using var rejected = await client.SendAsync(denied);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("explicitly request accuracy=approximate", await rejected.Content.ReadAsStringAsync());
        using var allowed = Request("/api/v1/analytics/" + path + selection + "&accuracy=approximate");
        using var response = await client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("metrics/activity", "metrics")]
    [InlineData("reports/overview", "reports")]
    public async Task PlansRequireBothAnAdministrativeClaimAndTheirReadScopes(string path, string readScope)
    {
        await using var factory = new ProjectContextHttpTests.Factory();
        using var client = factory.CreateClient();
        string url = "/api/v1/analytics/" + path + "/explain" + Range;
        using var anonymous = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        foreach (var (scope, role) in new[] { ("*", (string?)null), ("*", "reader"), (readScope, "admin"), ("plans", "admin") })
        {
            using var request = Request(url, scope: scope, role: role);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("handled", Assert.Single(response.Headers.GetValues("X-Test-Forbidden")));
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }
        using var allowed = Request(url, scope: "plans " + readScope);
        using var plan = await client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        using var document = JsonDocument.Parse(await plan.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(path.Split('/')[1], root.GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("raw", root.GetProperty("source").GetString());
        Assert.Equal("exact", root.GetProperty("accuracy").GetString());
        Assert.False(root.TryGetProperty("selectedRollup", out _));
        Assert.False(root.TryGetProperty("sql", out _));
        Assert.False(root.TryGetProperty("statistics", out _));
        Assert.Equal("alpha_hour", Assert.Single(root.GetProperty("candidates").EnumerateArray()).GetProperty("name").GetString());
        Assert.Contains("disabled", root.GetProperty("candidates")[0].GetProperty("reason").GetString());
        var slice = Assert.Single(root.GetProperty("slices").EnumerateArray());
        Assert.Equal("raw", slice.GetProperty("source").GetString());
        Assert.Equal("2090-01-01T00:00:00.001Z", slice.GetProperty("from").GetString());
        Assert.Equal("2090-01-01T03:00:00.001Z", slice.GetProperty("to").GetString());
    }

    [Fact]
    public async Task ExplainChoosesOnlyTheAuthenticatedProjectsReadyCandidatesWithoutQueryingStorage()
    {
        await using var factory = new ProjectContextHttpTests.Factory(customize: options =>
        {
            options.EnableAnalyticsRollupPlanning = true;
            options.AnalyticsRollupSourceIsAppendOnly = true;
        });
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<ProjectCatalog>();
        var alpha = catalog.Resolve("alpha")!;
        catalog.Storage.Rollups.MarkRoutable(Assert.Single(alpha.Rollups.Declared));
        using var request = Request("/api/v1/analytics/metrics/activity/explain" + Range);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("hybrid", root.GetProperty("source").GetString());
        Assert.Equal("alpha_hour", root.GetProperty("selectedRollup").GetString());
        Assert.Equal(3, root.GetProperty("slices").GetArrayLength());
        Assert.True(Assert.Single(root.GetProperty("candidates").EnumerateArray()).GetProperty("eligible").GetBoolean());
        Assert.DoesNotContain("beta_day", await response.Content.ReadAsStringAsync());
        using var rawRequest = Request("/api/v1/analytics/metrics/activity/explain" + Range + "&execution=raw&accuracy=exact");
        using var raw = await client.SendAsync(rawRequest);
        using var rawDocument = JsonDocument.Parse(await raw.Content.ReadAsStringAsync());
        Assert.Equal("raw", rawDocument.RootElement.GetProperty("source").GetString());
        Assert.Contains("requested", rawDocument.RootElement.GetProperty("candidates")[0].GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("metrics/missing", "alpha", "t", "", 404)]
    [InlineData("reports/missing", "alpha", "t", "", 404)]
    [InlineData("reports/overview", "beta", "t", "", 404)]
    [InlineData("metrics/activity", "alpha", null, "", 403)]
    [InlineData("reports/overview", "alpha", null, "", 403)]
    [InlineData("metrics/activity", "alpha", "t", "&execution=unknown", 400)]
    [InlineData("metrics/activity", "alpha", "t", "&accuracy=unknown", 400)]
    [InlineData("metrics/activity", "alpha", "t", "&traffic=unknown", 400)]
    [InlineData("metrics/activity", "alpha", "t", "&segment=missing", 404)]
    [InlineData("reports/overview", "alpha", "t", "&accuracy=unknown", 400)]
    [InlineData("reports/overview", "alpha", "t", "&execution=unknown", 400)]
    [InlineData("reports/overview", "alpha", "t", "&traffic=unknown", 400)]
    [InlineData("reports/overview", "alpha", "t", "&filter=category:x", 400)]
    [InlineData("reports/overview", "alpha", "t", "&groupBy=country", 400)]
    [InlineData("reports/overview", "alpha", "t", "&param.missing=x", 400)]
    [InlineData("metrics/activity", "alpha", "t", "&filter=category:x", 400)]
    public async Task ExplainKeepsTheSameDefinitionIdentityAndInputRejections(string path, string project, string? tenant, string suffix, int status)
    {
        await using var factory = new ProjectContextHttpTests.Factory();
        using var client = factory.CreateClient();
        using var request = Request("/api/v1/analytics/" + path + "/explain" + Range + suffix, project, tenant);
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 403)
        {
            Assert.Equal("handled", Assert.Single(response.Headers.GetValues("X-Test-Forbidden")));
        }
    }
}

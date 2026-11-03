using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
    [InlineData("reports/overview", "alpha", "t", "&accuracy=unknown", 400)]
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

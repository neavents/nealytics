using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Integration;

/// <summary>
/// Boots the real application against the real ClickHouse.
///
/// <b>Dimensions are deliberately not declared here.</b> Declaring one would have the schema
/// reconciler add that column to the target database — which for a developer running these locally
/// is the same <c>nealytics_core.global_events</c> the deployment writes to. The moment a test put
/// a value in it, the deployment's own service would refuse to boot, because it does not declare
/// that name. Hardcoding the deployment's names instead would put one product's vocabulary back
/// into an engine whose whole point is not having any.
///
/// So the declaration comes from the environment, exactly as it does in production:
///
/// <code>
/// TelemetryEngine__Dimensions__0__Name=article_id TelemetryEngine__Dimensions__0__Type=String \
/// TelemetryEngine__Dimensions__1__Name=section_id TelemetryEngine__Dimensions__1__Type=String \
/// TelemetryEngine__Dimensions__2__Name=table_id TelemetryEngine__Dimensions__2__Type=String \
///   dotnet test
/// </code>
///
/// Against an empty database none of that is needed. Against a database a deployment is using, it
/// is required, and the failure is loud and says exactly which column and how many rows — see
/// ClickHouseSchemaMigrator's refuse-to-start rules. That is the guard working, not a broken test.
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public TestWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("TelemetryEngine__JwtSymmetricKey",
            "this-is-a-test-jwt-key-at-least-32-bytes-long!!");
        Environment.SetEnvironmentVariable("TelemetryEngine__AllowedProjectKeys", "test-key-1,test-key-2");
        Environment.SetEnvironmentVariable("TelemetryEngine__ClickHouseConnectionString",
            ClickHouseTestSupport.ConnectionString);
        Environment.SetEnvironmentVariable("TelemetryEngine__WriteAheadLogDirectory",
            Path.Combine(Path.GetTempPath(), $"nealytics_wal_int_{Guid.NewGuid():N}"));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.Configure<TelemetryEngineOptions>(options =>
            {
                options.MemoryChannelCapacity = 100;
                options.DatabaseBatchCommitSize = 10;
                options.ForceFlushIntervalSeconds = 1;
                options.MaxQueryLimit = 100;
            });
        });

        builder.UseEnvironment("Testing");
    }
}

public abstract class IntegrationTestBase : IClassFixture<TestWebApplicationFactory>
{
    protected readonly TestWebApplicationFactory Factory;
    protected readonly HttpClient Client;

    protected IntegrationTestBase(TestWebApplicationFactory factory)
    {
        Factory = factory;
        Client = factory.CreateClient();
    }

    protected static string GetJwt(string projectId, string tenantId)
    {
        var keyBytes = Encoding.UTF8.GetBytes("this-is-a-test-jwt-key-at-least-32-bytes-long!!");
        var key = new SymmetricSecurityKey(keyBytes);
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[] { new Claim("project_id", projectId), new Claim("tenant_id", tenantId) };

        var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

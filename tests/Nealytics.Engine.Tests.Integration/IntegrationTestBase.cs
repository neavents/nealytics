using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Integration;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public TestWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("TelemetryEngine__JwtSymmetricKey",
            "this-is-a-test-jwt-key-at-least-32-bytes-long!!");
        Environment.SetEnvironmentVariable("TelemetryEngine__AllowedProjectKeys", "test-key-1,test-key-2");
        Environment.SetEnvironmentVariable("TelemetryEngine__ClickHouseConnectionString",
            "Host=127.0.0.1;Port=9100;Database=nealytics_core;User=default;Password=;");
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

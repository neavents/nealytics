using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Features.HttpImport;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class HttpImportIntegrationTests
{
    [Theory]
    [InlineData("managed", false)]
    [InlineData("custom", false)]
    [InlineData("managed", true)]
    [InlineData("custom", true)]
    public async Task ConditionalHttpChunksRetainConfirmedProgressAcrossChangeAndRestart(string path, bool mapped)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string project = "e6_" + suffix; string measure = "e6_amount_" + suffix;
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-http-integration-" + suffix);
        byte[] Event(int value) => Encoding.UTF8.GetBytes(mapped ? JsonSerializer.Serialize(new
        {
            source = new { id = Guid.NewGuid(), time = DateTime.UtcNow.AddHours(-1), tenant = "t", amount = value }
        }) : JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(), timestamp = DateTime.UtcNow.AddHours(-1), projectId = project,
            tenantId = "t", eventType = "action", measures = new Dictionary<string, string> { [measure] = value.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        }));
        byte[] first = [.. Event(2), 13, 10];
        byte[] original = [.. first, .. Event(4)];
        byte[] current = original;
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var origin = builder.Build();
        origin.MapMethods("/object", ["GET", "HEAD"], async context =>
        {
            Assert.Equal("Bearer private-source", context.Request.Headers.Authorization.ToString());
            Assert.Equal("private-query", context.Request.Query["selection"].ToString());
            byte[] bytes = current;
            string tag = "\"" + Convert.ToHexString(SHA256.HashData(bytes)) + "\"";
            context.Response.Headers.ETag = tag;
            if (context.Request.Headers.IfMatch.Count != 0 && context.Request.Headers.IfMatch.ToString() != tag)
            {
                context.Response.StatusCode = 412; return;
            }
            if (context.Request.Method == "HEAD") { context.Response.ContentLength = bytes.Length; return; }
            string range = context.Request.Headers.Range.ToString();
            Assert.StartsWith("bytes=", range);
            string[] coordinates = range[6..].Split('-');
            int start = int.Parse(coordinates[0], System.Globalization.CultureInfo.InvariantCulture);
            int end = int.Parse(coordinates[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(start, 0, bytes.Length - 1); Assert.InRange(end, start, bytes.Length - 1);
            context.Response.StatusCode = 206; context.Response.ContentLength = end - start + 1;
            context.Response.Headers.ContentRange = $"bytes {start}-{end}/{bytes.Length}";
            await context.Response.Body.WriteAsync(bytes.AsMemory(start, end - start + 1));
        });
        await origin.StartAsync();
        string url = origin.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/object?selection=private-query";
        void Configure(TelemetryEngineOptions options)
        {
            options.EnableImportJobs = true; options.ImportMaxRecords = 1;
            options.HttpSources = [new()
            {
                Name = "source", ProjectId = project, Url = url, AllowInsecureHttp = true,
                IdentityKey = Convert.ToBase64String(Enumerable.Repeat((byte)19, 32).ToArray()),
                Headers = new() { ["Authorization"] = "Bearer private-source" }
            }];
        }
        string route = "/api/v1/telemetry/import/http/source";
        Guid job = Guid.NewGuid(); Guid chunk = Guid.NewGuid();
        string firstRoute = route + "/jobs/" + job + "/chunks/" + chunk + "?offset=0";
        HttpObjectSnapshot snapshot;
        string confirmed;
        try
        {
            await using (var engine = new NdjsonImportIntegrationTests.Factory(project, measure, path, mapped,
                directory: directory, preserveWal: true, configure: Configure))
            {
                using var client = engine.CreateClient(); client.DefaultRequestHeaders.Add("X-Project-Key", "import-key");
                using var inspection = await client.PostAsync(route + "/inspect", null);
                Assert.Equal(HttpStatusCode.OK, inspection.StatusCode);
                snapshot = (await inspection.Content.ReadFromJsonAsync<HttpObjectSnapshot>())!;
                Assert.Equal(original.Length, snapshot.Bytes);
                using var accepted = await client.PostAsJsonAsync(firstRoute, snapshot);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); confirmed = await accepted.Content.ReadAsStringAsync();
                using var progress = JsonDocument.Parse(confirmed);
                Assert.Equal(first.Length, progress.RootElement.GetProperty("nextByteOffset").GetInt64());
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (await ClickHouseTestSupport.CountAsync(project) != 1) await Task.Delay(25, deadline.Token);
                current = Event(100);
                using var changed = await client.PostAsJsonAsync(firstRoute, snapshot);
                Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
                Assert.Equal("source_changed", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
                using var status = await client.GetAsync("/api/v1/telemetry/import/jobs/" + job);
                Assert.Equal(confirmed, await status.Content.ReadAsStringAsync());
                current = original;
            }
            await using (var restarted = new NdjsonImportIntegrationTests.Factory(project, measure, path, mapped,
                directory: directory, preserveWal: true, configure: Configure))
            {
                using var client = restarted.CreateClient(); client.DefaultRequestHeaders.Add("X-Project-Key", "import-key");
                using var repeated = await client.PostAsJsonAsync(firstRoute, snapshot);
                Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
                Assert.Equal(confirmed, await repeated.Content.ReadAsStringAsync());
                using var last = await client.PostAsJsonAsync(route + "/jobs/" + job + "/chunks/" + Guid.NewGuid() + "?offset=" + first.Length, snapshot);
                Assert.Equal(HttpStatusCode.Accepted, last.StatusCode);
                using var result = JsonDocument.Parse(await last.Content.ReadAsStringAsync());
                Assert.Equal("complete", result.RootElement.GetProperty("state").GetString());
                Assert.Equal(2, result.RootElement.GetProperty("acceptedRecords").GetInt64());
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (await ClickHouseTestSupport.CountAsync(project) != 2) await Task.Delay(25, deadline.Token);
                var token = new JwtSecurityToken(claims: [new Claim("project_id", project), new Claim("tenant_id", "t")],
                    expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes("this-is-a-test-jwt-key-at-least-32-bytes-long!!")), SecurityAlgorithms.HmacSha256));
                using var query = new HttpRequestMessage(HttpMethod.Get, "/api/v1/analytics/metrics/total");
                query.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
                using var response = await client.SendAsync(query); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var total = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("6", total.RootElement.GetProperty("value").GetString());
                foreach (string file in Directory.GetFiles(Path.Combine(directory, "import-jobs"), "*.json"))
                {
                    string manifest = await File.ReadAllTextAsync(file);
                    Assert.DoesNotContain("private-query", manifest); Assert.DoesNotContain("private-source", manifest);
                    Assert.DoesNotContain(url, manifest);
                }
            }
        }
        finally
        {
            await origin.StopAsync();
            await ClickHouseTestSupport.DeleteProjectsAsync(project);
            await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.global_events DROP COLUMN IF EXISTS {measure}");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}

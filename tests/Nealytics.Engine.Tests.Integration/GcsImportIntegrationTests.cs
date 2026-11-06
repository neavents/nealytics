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
using Nealytics.Engine.Features.GcsImport;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class GcsImportIntegrationTests
{
    [Theory]
    [InlineData("managed", false)]
    [InlineData("custom", false)]
    [InlineData("managed", true)]
    [InlineData("custom", true)]
    public async Task RetainedGenerationsResumeAcrossReplacementAndRestartWhileChangedMetadataAndDeletionFenceRetries(string path, bool mapped)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string project = "e7_" + suffix; string measure = "e7_amount_" + suffix;
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-gcs-integration-" + suffix);
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
        var versions = new Dictionary<long, byte[]> { [17] = original };
        var metadataVersions = new Dictionary<long, long> { [17] = 3 };
        long current = 17;
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var origin = builder.Build();
        origin.MapMethods("/{bucket}/{**key}", ["GET", "HEAD"], async context =>
        {
            Assert.Equal("fixture-bucket", context.Request.RouteValues["bucket"]);
            Assert.Equal("events/file.ndjson", context.Request.RouteValues["key"]);
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=fixture-access/", context.Request.Headers.Authorization.ToString());
            long selected = context.Request.Query.TryGetValue("generation", out var generation)
                ? long.Parse(generation.ToString(), System.Globalization.CultureInfo.InvariantCulture) : current;
            if (!versions.TryGetValue(selected, out byte[]? bytes)) { context.Response.StatusCode = 404; return; }
            long meta = metadataVersions[selected];
            string tag = "\"" + Convert.ToHexString(SHA256.HashData(bytes)) + "\"";
            if ((context.Request.Headers.IfMatch.Count != 0 && context.Request.Headers.IfMatch.ToString() != tag)
                || (context.Request.Headers.TryGetValue("x-goog-if-generation-match", out var pinned) && pinned != selected.ToString())
                || (context.Request.Headers.TryGetValue("x-goog-if-metageneration-match", out var pinnedMeta) && pinnedMeta != meta.ToString()))
            {
                context.Response.StatusCode = 412; return;
            }
            context.Response.Headers.ETag = tag;
            context.Response.Headers["x-goog-generation"] = selected.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["x-goog-metageneration"] = meta.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["x-goog-stored-content-length"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["x-goog-stored-content-encoding"] = "identity";
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
        string url = origin.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        void Configure(TelemetryEngineOptions options)
        {
            options.EnableImportJobs = true; options.ImportMaxRecords = 1;
            options.GcsSources = [new()
            {
                Name = "source", ProjectId = project, Endpoint = url, AllowInsecureHttp = true,
                Bucket = "fixture-bucket", Prefix = "events/", AccessKey = "fixture-access", SecretKey = "fixture-secret",
                IdentityKey = Convert.ToBase64String(Enumerable.Repeat((byte)19, 32).ToArray())
            }];
        }
        string route = "/api/v1/telemetry/import/gcs/source";
        Guid job = Guid.NewGuid(); Guid chunk = Guid.NewGuid();
        string firstRoute = route + "/jobs/" + job + "/chunks/" + chunk + "?offset=0";
        GcsObjectSnapshot snapshot;
        string confirmed;
        try
        {
            await using (var engine = new NdjsonImportIntegrationTests.Factory(project, measure, path, mapped,
                directory: directory, preserveWal: true, configure: Configure))
            {
                using var client = engine.CreateClient(); client.DefaultRequestHeaders.Add("X-Project-Key", "import-key");
                using var inspection = await client.PostAsJsonAsync(route + "/inspect", new { key = "events/file.ndjson" });
                Assert.Equal(HttpStatusCode.OK, inspection.StatusCode);
                snapshot = (await inspection.Content.ReadFromJsonAsync<GcsObjectSnapshot>())!;
                Assert.Equal(original.Length, snapshot.Bytes);
                using var accepted = await client.PostAsJsonAsync(firstRoute, snapshot);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); confirmed = await accepted.Content.ReadAsStringAsync();
                using var progress = JsonDocument.Parse(confirmed);
                Assert.Equal(first.Length, progress.RootElement.GetProperty("nextByteOffset").GetInt64());
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (await ClickHouseTestSupport.CountAsync(project) != 1) await Task.Delay(25, deadline.Token);
                versions[18] = Event(100); metadataVersions[18] = 1; current = 18;
                using var retained = await client.PostAsJsonAsync(firstRoute, snapshot);
                Assert.Equal(HttpStatusCode.Accepted, retained.StatusCode);
                Assert.Equal(confirmed, await retained.Content.ReadAsStringAsync());
                Assert.Equal(1, await ClickHouseTestSupport.CountAsync(project));
                using var inspectionLatest = await client.PostAsJsonAsync(route + "/inspect", new { key = "events/file.ndjson" });
                var latest = (await inspectionLatest.Content.ReadFromJsonAsync<GcsObjectSnapshot>())!;
                Assert.Equal(18, latest.Generation); Assert.NotEqual(snapshot.SourceId, latest.SourceId);
                using var mismatch = await client.PostAsJsonAsync(route + "/jobs/" + job + "/chunks/" + Guid.NewGuid() + "?offset=0", latest);
                Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
                Assert.Equal("source_conflict", (await mismatch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
                using var status = await client.GetAsync("/api/v1/telemetry/import/jobs/" + job);
                Assert.Equal(confirmed, await status.Content.ReadAsStringAsync());
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
                metadataVersions[17] = 4;
                using var changedMetadata = await client.PostAsJsonAsync(firstRoute, snapshot);
                Assert.Equal(HttpStatusCode.Conflict, changedMetadata.StatusCode);
                Assert.Equal("source_changed", (await changedMetadata.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
                versions.Remove(17);
                using var deleted = await client.PostAsJsonAsync(firstRoute, snapshot);
                Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
                Assert.Equal("source_object_not_found", (await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
                using var persisted = await client.GetAsync("/api/v1/telemetry/import/jobs/" + job);
                Assert.Equal(result.RootElement.GetRawText(), await persisted.Content.ReadAsStringAsync());
                Assert.Equal(2, await ClickHouseTestSupport.CountAsync(project));
                foreach (string file in Directory.GetFiles(Path.Combine(directory, "import-jobs"), "*.json"))
                {
                    string manifest = await File.ReadAllTextAsync(file);
                    Assert.DoesNotContain("fixture-bucket", manifest); Assert.DoesNotContain("fixture-secret", manifest);
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

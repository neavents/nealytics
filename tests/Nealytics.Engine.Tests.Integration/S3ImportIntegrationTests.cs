using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Features.S3Import;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class S3ImportIntegrationTests : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly string _access = "fixture" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _secret = Guid.NewGuid().ToString("N");
    private IContainer _store = null!;
    private string Endpoint => "http://" + _store.Hostname + ":" + _store.GetMappedPublicPort(9000);
    private string Bucket => "s3-" + _suffix;

    public async Task InitializeAsync()
    {
        _store = new ContainerBuilder(Environment.GetEnvironmentVariable("NEALYTICS_S3_FIXTURE_IMAGE") ?? "nealytics-s3-fixture:9e49d5e")
            .WithLabel("nealytics.scope", "s3-integration-" + _suffix)
            .WithPortBinding(9000, true)
            .WithEnvironment("MINIO_ROOT_USER", _access)
            .WithEnvironment("MINIO_ROOT_PASSWORD", _secret)
            .WithEnvironment("MINIO_BROWSER", "off")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(9000).ForPath("/minio/health/ready")))
            .Build();
        await _store.StartAsync();
        await Seed(HttpMethod.Put, "/" + Bucket, []);
        await Seed(HttpMethod.Put, "/" + Bucket + "?versioning=", Encoding.UTF8.GetBytes(
            "<VersioningConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Status>Enabled</Status></VersioningConfiguration>"), "application/xml");
    }

    [Theory]
    [InlineData("managed", false)]
    [InlineData("custom", false)]
    [InlineData("managed", true)]
    [InlineData("custom", true)]
    public async Task VersionedS3ImportsResumeAcrossEngineRestartWithoutChangingThePinnedEventSet(string execution, bool mapped)
    {
        string project = "s3_" + _suffix;
        string measure = "s3_amount_" + _suffix;
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-s3-restart-" + Guid.NewGuid().ToString("N"));
        string key = "events/日本語 +%.ndjson";
        string objectPath = "/" + Bucket + "/" + Uri.EscapeDataString(key).Replace("%2F", "/", StringComparison.Ordinal);
        var source = new S3SourceOptions
        {
            Name = "source", ProjectId = project, Endpoint = Endpoint, Bucket = Bucket, Region = "us-east-1",
            Prefix = "events/", AccessKey = _access, SecretKey = _secret, AllowInsecureHttp = true
        };
        string Event(int value) => mapped ? JsonSerializer.Serialize(new
        {
            source = new { id = Guid.NewGuid(), time = DateTime.UtcNow.AddHours(-1), tenant = "t", amount = value }
        }) : JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(), timestamp = DateTime.UtcNow.AddHours(-1), projectId = project, tenantId = "t",
            eventType = "action", measures = new Dictionary<string, string> { [measure] = value.ToString(CultureInfo.InvariantCulture) }
        });
        string first = Event(2) + "\r\n"; string second = Event(4);
        byte[] original = Encoding.UTF8.GetBytes(first + second);
        await Seed(HttpMethod.Put, objectPath, original, "application/x-ndjson");
        Guid job = Guid.NewGuid(); Guid firstId = Guid.NewGuid();
        string route = "/api/v1/telemetry/import/s3/source";
        string firstRoute = route + "/jobs/" + job + "/chunks/" + firstId + "?offset=0";
        S3ObjectSnapshot snapshot;
        string receipt;
        try
        {
            await using (var factory = new NdjsonImportIntegrationTests.Factory(project, measure, execution, mapped,
                source: source, directory: directory, preserveWal: true))
            {
                using var client = factory.CreateClient();
                client.DefaultRequestHeaders.Add("X-Project-Key", "import-key");
                using var inspect = await client.PostAsJsonAsync(route + "/inspect", new S3ObjectRequest(key, null));
                Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
                snapshot = JsonSerializer.Deserialize(await inspect.Content.ReadAsStringAsync(), TelemetryAotContext.Default.S3ObjectSnapshot)!;
                Assert.Equal(original.Length, snapshot.Bytes);
                Assert.False(string.IsNullOrEmpty(snapshot.VersionId));
                await Seed(HttpMethod.Put, objectPath, Encoding.UTF8.GetBytes(Event(100)), "application/x-ndjson");
                using var response = await client.PostAsJsonAsync(firstRoute, snapshot);
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                receipt = await response.Content.ReadAsStringAsync();
                using var ready = JsonDocument.Parse(receipt);
                Assert.Equal("ready", ready.RootElement.GetProperty("state").GetString());
                Assert.Equal(Encoding.UTF8.GetByteCount(first), ready.RootElement.GetProperty("nextByteOffset").GetInt64());
                await WaitCount(project, 1);
            }
            await using (var restarted = new NdjsonImportIntegrationTests.Factory(project, measure, execution, mapped,
                source: source, directory: directory, preserveWal: true))
            {
                using var client = restarted.CreateClient();
                client.DefaultRequestHeaders.Add("X-Project-Key", "import-key");
                using var status = await client.GetAsync("/api/v1/telemetry/import/jobs/" + job);
                Assert.Equal(HttpStatusCode.OK, status.StatusCode);
                Assert.Equal(receipt, await status.Content.ReadAsStringAsync());
                using var repeated = await client.PostAsJsonAsync(firstRoute, snapshot);
                Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
                Assert.Equal(receipt, await repeated.Content.ReadAsStringAsync());
                string next = route + "/jobs/" + job + "/chunks/" + Guid.NewGuid() + "?offset=" + Encoding.UTF8.GetByteCount(first);
                using var final = await client.PostAsJsonAsync(next, snapshot);
                Assert.Equal(HttpStatusCode.Accepted, final.StatusCode);
                using var completed = JsonDocument.Parse(await final.Content.ReadAsStringAsync());
                Assert.Equal("complete", completed.RootElement.GetProperty("state").GetString());
                Assert.Equal(2, completed.RootElement.GetProperty("acceptedRecords").GetInt64());
                Assert.Equal(original.Length, completed.RootElement.GetProperty("nextByteOffset").GetInt64());
                await WaitCount(project, 2);
                using var latest = await client.PostAsJsonAsync(route + "/inspect", new S3ObjectRequest(key, null));
                Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
                var changed = JsonSerializer.Deserialize(await latest.Content.ReadAsStringAsync(), TelemetryAotContext.Default.S3ObjectSnapshot)!;
                Assert.NotEqual(snapshot.SourceId, changed.SourceId);
                using var refused = await client.PostAsJsonAsync(route + "/jobs/" + job + "/chunks/" + Guid.NewGuid() + "?offset=0", changed);
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                using var query = new HttpRequestMessage(HttpMethod.Get, "/api/v1/analytics/metrics/total");
                query.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(project));
                using var aggregate = await client.SendAsync(query);
                Assert.Equal(HttpStatusCode.OK, aggregate.StatusCode);
                using var result = JsonDocument.Parse(await aggregate.Content.ReadAsStringAsync());
                Assert.Equal("6", result.RootElement.GetProperty("value").GetString());
                Assert.Equal(2, await ClickHouseTestSupport.CountAsync(project));
            }
        }
        finally
        {
            await ClickHouseTestSupport.DeleteProjectsAsync(project);
            await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.global_events DROP COLUMN IF EXISTS {measure}");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task WaitCount(string project, long count)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        while (await ClickHouseTestSupport.CountAsync(project) != count) await Task.Delay(25, deadline.Token);
    }

    private static string Token(string project)
    {
        var token = new JwtSecurityToken(claims: [new Claim("project_id", project), new Claim("tenant_id", "t")],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("this-is-a-test-jwt-key-at-least-32-bytes-long!!")), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task Seed(HttpMethod method, string path, byte[] body, string? contentType = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(method, Endpoint + path) { Content = new ByteArrayContent(body) };
        if (contentType is not null) request.Content.Headers.ContentType = new(contentType);
        string instant = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string date = instant[..8]; string hash = Convert.ToHexStringLower(SHA256.HashData(body));
        request.Headers.Add("x-amz-date", instant); request.Headers.Add("x-amz-content-sha256", hash);
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = request.RequestUri!.Authority, ["x-amz-content-sha256"] = hash, ["x-amz-date"] = instant
        };
        if (contentType == "application/xml")
        {
            string checksum = Convert.ToBase64String(MD5.HashData(body));
            request.Content.Headers.ContentMD5 = Convert.FromBase64String(checksum);
            headers["content-md5"] = checksum;
        }
        string signed = string.Join(';', headers.Keys);
        string canonical = method.Method + "\n" + request.RequestUri.AbsolutePath + "\n" + request.RequestUri.Query.TrimStart('?') + "\n"
            + string.Concat(headers.Select(header => header.Key + ":" + header.Value + "\n")) + "\n" + signed + "\n" + hash;
        string scope = date + "/us-east-1/s3/aws4_request";
        string message = "AWS4-HMAC-SHA256\n" + instant + "\n" + scope + "\n" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        byte[] signing = Encoding.UTF8.GetBytes("AWS4" + _secret);
        foreach (string part in new[] { date, "us-east-1", "s3", "aws4_request" }) signing = HMACSHA256.HashData(signing, Encoding.UTF8.GetBytes(part));
        string signature = Convert.ToHexStringLower(HMACSHA256.HashData(signing, Encoding.UTF8.GetBytes(message)));
        request.Headers.TryAddWithoutValidation("Authorization", "AWS4-HMAC-SHA256 Credential=" + _access + "/" + scope
            + ",SignedHeaders=" + signed + ",Signature=" + signature);
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, "S3 fixture upload failed: " + (int)response.StatusCode + " " + await response.Content.ReadAsStringAsync());
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();
}

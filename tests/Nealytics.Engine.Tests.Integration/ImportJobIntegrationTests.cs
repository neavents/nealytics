using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class ImportJobIntegrationTests
{
    [Theory]
    [InlineData("managed", false)]
    [InlineData("custom", false)]
    [InlineData("managed", true)]
    [InlineData("custom", true)]
    public async Task ConfirmedChunkRetriesPreserveTheStoredEventSetAndAggregate(string path, bool mapped)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string project = "d1_" + suffix;
        string measure = "d1_amount_" + suffix;
        await using var factory = new NdjsonImportIntegrationTests.Factory(project, measure, path, mapped, jobs: true);
        using var client = factory.CreateClient();
        string Event(int value) => mapped ? JsonSerializer.Serialize(new
        {
            source = new { id = Guid.NewGuid(), time = DateTime.UtcNow.AddHours(-1), tenant = "t", amount = value }
        }) : JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(), timestamp = DateTime.UtcNow.AddHours(-1), projectId = project,
            tenantId = "t", eventType = "action", measures = new Dictionary<string, string> { [measure] = value.ToString() }
        });
        string first = Event(2) + "\r\n";
        string second = Event(4);
        long total = Encoding.UTF8.GetByteCount(first + second);
        string source = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first + second)));
        Guid job = Guid.NewGuid();
        string route = "/api/v1/telemetry/import/jobs/" + job;
        HttpRequestMessage Chunk(Guid chunk, string body, long offset) {
            var request = new HttpRequestMessage(HttpMethod.Post, route + "/chunks/" + chunk + "?offset=" + offset + "&k=import-key")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson")
            };
            request.Headers.Add("X-Import-Source", source);
            request.Headers.Add("X-Import-Source-Bytes", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return request;
        }
        try
        {
            string? latest = null;
            foreach (var chunk in new[] { (Guid.NewGuid(), first, 0L, "ready"), (Guid.NewGuid(), second, (long)Encoding.UTF8.GetByteCount(first), "complete") })
            {
                using var request = Chunk(chunk.Item1, chunk.Item2, chunk.Item3);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                latest = await response.Content.ReadAsStringAsync();
                using var result = JsonDocument.Parse(latest);
                Assert.Equal(chunk.Item4, result.RootElement.GetProperty("state").GetString());
                using var retry = Chunk(chunk.Item1, chunk.Item2, chunk.Item3);
                using var repeated = await client.SendAsync(retry);
                Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
                Assert.Equal(latest, await repeated.Content.ReadAsStringAsync());
            }
            using var status = await client.GetAsync(route + "?k=import-key");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.Equal(latest, await status.Content.ReadAsStringAsync());
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
            while (await ClickHouseTestSupport.CountAsync(project) != 2) await Task.Delay(25, deadline.Token);
            var token = new JwtSecurityToken(claims: [new Claim("project_id", project), new Claim("tenant_id", "t")],
                expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes("this-is-a-test-jwt-key-at-least-32-bytes-long!!")), SecurityAlgorithms.HmacSha256));
            using var query = new HttpRequestMessage(HttpMethod.Get, "/api/v1/analytics/metrics/total");
            query.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            using var aggregate = await client.SendAsync(query);
            Assert.Equal(HttpStatusCode.OK, aggregate.StatusCode);
            using var metric = JsonDocument.Parse(await aggregate.Content.ReadAsStringAsync());
            Assert.Equal("6", metric.RootElement.GetProperty("value").GetString());
            Assert.Equal("raw", metric.RootElement.GetProperty("source").GetString());
            Assert.Equal(2, await ClickHouseTestSupport.CountAsync(project));
        }
        finally
        {
            await factory.DisposeAsync();
            await ClickHouseTestSupport.DeleteProjectsAsync(project);
            await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.global_events DROP COLUMN IF EXISTS {measure}");
        }
    }
}

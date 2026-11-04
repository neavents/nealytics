using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class CsvImportIntegrationTests
{
    [Theory]
    [InlineData("managed")]
    [InlineData("custom")]
    public async Task QuotedCsvPersistsItsExactMappedPrefixAndRetainsQueryTotalsAcrossRestart(string path)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string project = "e5_" + suffix;
        string measure = "e5_amount_" + suffix;
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-csv-integration-" + suffix);
        string header = "\uFEFF\"event,id\",time,tenant,amount,details\r\n";
        string Row(int amount) => Guid.NewGuid().ToString() + "," + DateTime.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture)
            + ",t," + amount.ToString(CultureInfo.InvariantCulture) + ",\"日本語, \"\"quoted\"\"\r\nnext\"\r\n";
        string prefix = header + Row(2) + Row(4);
        void Configure(TelemetryEngineOptions options)
        {
            options.EnableCsvImport = true;
            options.EnableNdjsonImport = false;
            options.CsvSourceMappings[project] = new()
            {
                Fields = new()
                {
                    ["eventId"] = new() { Path = ["event,id"] },
                    ["timestamp"] = new() { Path = ["time"] },
                    ["projectId"] = new() { Default = project },
                    ["tenantId"] = new() { Path = ["tenant"] },
                    ["eventType"] = new() { Default = "action" },
                    ["measures." + measure] = new() { Path = ["amount"] },
                    ["metadataJson"] = new() { Path = ["details"] }
                }
            };
        }
        try
        {
            await using (var factory = new NdjsonImportIntegrationTests.Factory(project, measure, path, false,
                directory: directory, preserveWal: true, configure: Configure))
            {
                using var client = factory.CreateClient();
                using var response = await client.PostAsync("/api/v1/telemetry/import/csv?k=import-key",
                    new StringContent(prefix + "invalid,row\n" + Row(100), Encoding.UTF8, "text/csv"));
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(2, receipt.RootElement.GetProperty("acceptedRecords").GetInt32());
                Assert.Equal(3, receipt.RootElement.GetProperty("nextRecord").GetInt32());
                Assert.Equal(Encoding.UTF8.GetByteCount(prefix), receipt.RootElement.GetProperty("nextByteOffset").GetInt64());
                Assert.Equal("column_count", receipt.RootElement.GetProperty("error").GetString());
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (await ClickHouseTestSupport.CountAsync(project) != 2) await Task.Delay(25, deadline.Token);
            }
            await using var restarted = new NdjsonImportIntegrationTests.Factory(project, measure, path, false,
                directory: directory, preserveWal: true, configure: Configure);
            using var restartedClient = restarted.CreateClient();
            var token = new JwtSecurityToken(claims: [new Claim("project_id", project), new Claim("tenant_id", "t")],
                expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes("this-is-a-test-jwt-key-at-least-32-bytes-long!!")), SecurityAlgorithms.HmacSha256));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/analytics/metrics/total");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            using var query = await restartedClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, query.StatusCode);
            using var result = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
            Assert.Equal("6", result.RootElement.GetProperty("value").GetString());
            Assert.Equal("raw", result.RootElement.GetProperty("source").GetString());
            Assert.Equal(2, await ClickHouseTestSupport.CountAsync(project));
        }
        finally
        {
            await ClickHouseTestSupport.DeleteProjectsAsync(project);
            await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.global_events DROP COLUMN IF EXISTS {measure}");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}

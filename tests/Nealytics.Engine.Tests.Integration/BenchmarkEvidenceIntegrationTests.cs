using System.Reflection;
using System.Text.Json;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class BenchmarkEvidenceIntegrationTests
{
    [Theory]
    [InlineData("track", "0", 1)]
    [InlineData("track", "10", 1)]
    [InlineData("beacon", "0", 3)]
    [InlineData("beacon", "10", 3)]
    public async Task HarnessMeasuresRealKestrelAcknowledgmentAndClickHouseVisibility(
        string mode, string rate, int eventsPerRequest)
    {
        await using TestWebApplicationFactory factory = new();
        factory.UseKestrel(0);
        using HttpClient client = factory.CreateClient();
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-bench-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string manifestPath = Path.Combine(directory, "manifest.json");
        List<string> tenants = [];

        try
        {
            string[] arguments =
            [
                "--url", client.BaseAddress!.ToString(),
                "--mode", mode,
                "--concurrency", "2",
                "--requests", "3",
                "--warmup", "0",
                "--rate", rate,
                "--beacon-batch", "3",
                "--visibility-every", "1",
                "--ch", ClickHouseTestSupport.ConnectionString,
                "--out", Path.Combine(directory, "report.md"),
                "--manifest", manifestPath
            ];

            MethodInfo entry = typeof(BenchOptions).Assembly.GetType("Program")!
                .GetMethod("<Main>$", BindingFlags.Static | BindingFlags.NonPublic)!;
            Task<int> execution = (Task<int>)entry.Invoke(null, [arguments])!;
            int result = await execution;

            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            JsonElement root = document.RootElement;
            JsonElement level = root.GetProperty("levels")[0];
            tenants.Add(level.GetProperty("tenantId").GetString()!);

            Assert.Equal(0, result);
            Assert.Equal(mode, root.GetProperty("mode").GetString());
            Assert.Equal(eventsPerRequest, root.GetProperty("eventsPerRequest").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("clickHouseVersion").GetString()));
            Assert.Equal(3, level.GetProperty("ok").GetInt64());
            Assert.Equal(0, level.GetProperty("errors").GetInt64());
            Assert.Equal(3 * eventsPerRequest, level.GetProperty("stored").GetInt64());
            Assert.Equal(3, level.GetProperty("visibilitySamples").GetInt64());
            Assert.Equal(0, level.GetProperty("visibilityDropped").GetInt64());
            Assert.Equal(0, level.GetProperty("visibilityTimeouts").GetInt64());
            Assert.True(level.GetProperty("p99").GetDouble() > 0);
            Assert.True(level.GetProperty("visibilityP99").GetDouble() >= level.GetProperty("p50").GetDouble());
            Assert.True(level.GetProperty("clientWorkingSetBytes").GetInt64() > 0);
            Assert.Contains("Scheduled p99", await File.ReadAllTextAsync(Path.Combine(directory, "report.md")));
        }
        finally
        {
            foreach (string tenant in tenants)
            {
                Assert.Matches("^bench-(track|beacon)-c2-[0-9]+$", tenant);
                await ClickHouseTestSupport.ExecuteAsync(
                    "ALTER TABLE nealytics_core.global_events DELETE WHERE project_id = 'bench'"
                    + $" AND tenant_id = '{tenant}' SETTINGS mutations_sync = 2");
            }

            Directory.Delete(directory, true);
        }
    }
}

namespace Nealytics.Engine.Bench.Measurements;

using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

public sealed class BenchmarkManifest
{
    public int FormatVersion { get; init; } = 1;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Revision { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string ClientRuntime { get; init; } = RuntimeInformation.FrameworkDescription;
    public string ClientOperatingSystem { get; init; } = RuntimeInformation.OSDescription;
    public string ClientArchitecture { get; init; } = RuntimeInformation.ProcessArchitecture.ToString();
    public int ClientProcessorCount { get; init; } = Environment.ProcessorCount;
    public int WarmupRequests { get; init; }
    public int RequestsPerLevel { get; init; }
    public double DurationSeconds { get; init; }
    public double OfferedRequestsPerSecond { get; init; }
    public int EventsPerRequest { get; init; }
    public bool VerifyStoredCount { get; init; }
    public string? ClickHouseVersion { get; init; }
    public Dictionary<string, string> DeclaredEngineSettings { get; init; } = [];
    public List<LevelResult> Levels { get; init; } = [];

    public static string SafeTarget(string target)
    {
        UriBuilder uri = new(target)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The benchmark target must use HTTP or HTTPS.", nameof(target));
        }

        return uri.Uri.AbsoluteUri;
    }

    public static Dictionary<string, string> CaptureSettings(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        string[] names =
        [
            "DatabaseBatchCommitSize", "ForceFlushIntervalSeconds", "MemoryChannelCapacity",
            "RateLimitPermitCount", "RateLimitWindowSeconds", "RateLimitQueueSize",
            "ConnectionPoolSize", "WalFileBufferBytes", "EnableAsyncInsert", "EnableWireCompression",
            "WalBackend", "IngestionPipeline"
        ];

        Dictionary<string, string> settings = new(StringComparer.Ordinal);

        foreach (string name in names)
        {
            string? value = read("TelemetryEngine__" + name);

            if (value is not null)
            {
                settings.Add(name, value);
            }
        }

        return settings;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BenchmarkManifest))]
public partial class BenchmarkJsonContext : JsonSerializerContext
{
}

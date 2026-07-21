namespace Nealytics.Engine.Infrastructure.Configuration;

public sealed class TelemetryEngineOptions
{
    public string ClickHouseConnectionString { get; set; } = "Host=127.0.0.1;Port=9000;Database=nealytics_core;";
    public string WriteAheadLogDirectory { get; set; } = "/var/log/nealytics_engine/";
    public int MemoryChannelCapacity { get; set; } = 100_000;
    public int DatabaseBatchCommitSize { get; set; } = 10_000;
    public int ForceFlushIntervalSeconds { get; set; } = 3;
    public string AllowedProjectKeys { get; set; } = string.Empty;
    public string JwtSymmetricKey { get; set; } = string.Empty;
    public int MaxRequestBodyBytes { get; set; } = 1_048_576;
    public int MaxQueryLimit { get; set; } = 10_000;
    public int RateLimitPermitCount { get; set; } = 1_000;
    public int RateLimitWindowSeconds { get; set; } = 10;
    public int RateLimitQueueSize { get; set; } = 500;
    public string CorsAllowedOrigins { get; set; } = string.Empty;
    public int JwtClockSkewSeconds { get; set; } = 30;
    public int MaxInsertRetries { get; set; } = 5;
    public int RetryBackoffCeilingMs { get; set; } = 30_000;
    public int DefaultSessionQueryRangeHours { get; set; } = 24;
    public int ConnectionPoolSize { get; set; } = 16;
    public int WalReplayRetryDelayMs { get; set; } = 10_000;
    public int WalFileBufferBytes { get; set; } = 65_536;
    public bool EnableWireCompression { get; set; } = true;
    public bool EnableAsyncInsert { get; set; } = true;
    public int MaxConcurrentConnections { get; set; } = 20_000;
    public bool EnableRequestDecompression { get; set; } = true;
    public bool EnablePrometheusScrape { get; set; } = false;
}

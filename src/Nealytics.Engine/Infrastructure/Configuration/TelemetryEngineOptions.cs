namespace Nealytics.Engine.Infrastructure.Configuration;

using System.Collections.Generic;

/// <summary>
/// One analytics dimension, declared by the deployment rather than by the engine.
///
/// The engine ships this list empty. A deployment names its own dimensions in configuration —
/// <c>TelemetryEngine__Dimensions__0__Name</c> and friends — which is the whole reason the engine
/// no longer contains any customer's vocabulary. See PLAN-domain-agnostic-dimensions.md §4.1.
/// </summary>
public sealed class DimensionOptions
{
    /// <summary>snake_case column name, <c>[a-z][a-z0-9_]{0,62}</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>One of String | LowCardinality | UInt64 | Int64 | DateTime.</summary>
    public string Type { get; set; } = "String";

    /// <summary>
    /// Retires the dimension without destroying it: the column and every row stay, ingestion
    /// refuses the name, and the query API stops offering it.
    ///
    /// Retirement has to be an explicit act because *absence is what a mistake looks like* — one
    /// deleted config line would otherwise start a silent hole in the data at the same instant it
    /// blanked the widget that would have shown you.
    /// </summary>
    public bool Retired { get; set; }
}

public sealed class TelemetryEngineOptions
{
    /// <summary>
    /// Dimensions this deployment declares. Empty in the shipped <c>appsettings.json</c> on
    /// purpose — see <see cref="DimensionOptions"/>.
    /// </summary>
    public List<DimensionOptions> Dimensions { get; set; } = [];

    /// <summary>
    /// Ceiling on declared dimensions. The registry issues DDL from this list, so a config bug
    /// must not be able to add columns without limit.
    /// </summary>
    public int MaxDimensions { get; set; } = 64;

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

namespace Nealytics.Engine.Infrastructure.Configuration;

using System.Collections.Generic;

public sealed class DimensionOptions
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "String";

    public bool Retired { get; set; }
}

public sealed class MeasureOptions
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "";

    public string Aggregations { get; set; } = "sum,avg,min,max,count";

    public double? Minimum { get; set; }

    public double? Maximum { get; set; }

    public bool Retired { get; set; }

    public bool ServerOnly { get; set; }

    public string UnitDimension { get; set; } = "";
}

public sealed class TenantAttributeOptions
{
    public string Name { get; set; } = "";
}

public sealed class IngestionKeyOptions
{
    public string Key { get; set; } = "";

    public string Scope { get; set; } = "";

    public string ProjectId { get; set; } = "";

    public string EventTypes { get; set; } = "";
}

public sealed class ProjectKeyOptions
{
    public string Key { get; set; } = "";

    public string ProjectId { get; set; } = "";
}

public sealed class EventTypeRetentionOptions
{
    public string EventType { get; set; } = "";

    public int Days { get; set; }
}

public sealed class RollupOptions
{
    public string Name { get; set; } = "";

    public string Grain { get; set; } = "day";

    public string EventTypes { get; set; } = "";

    public string Dimensions { get; set; } = "";

    public string Measures { get; set; } = "";

    public string TenantAttributes { get; set; } = "";
}

public sealed class TelemetryEngineOptions
{
    public List<DimensionOptions> Dimensions { get; set; } = [];

    public List<MeasureOptions> Measures { get; set; } = [];

    public List<RollupOptions> Rollups { get; set; } = [];

    public List<TenantAttributeOptions> TenantAttributes { get; set; } = [];

    public List<IngestionKeyOptions> IngestionKeys { get; set; } = [];

    public string ServerEventTypes { get; set; } = string.Empty;

    public int PublicKeyTimestampToleranceSeconds { get; set; } = 300;

    public int MaxTenantAttributeBatch { get; set; } = 10_000;

    public string AliasEventType { get; set; } = string.Empty;

    public string ClusterName { get; set; } = string.Empty;

    public string JwtIssuer { get; set; } = string.Empty;

    public string JwtAudience { get; set; } = string.Empty;

    public string JwtPublicKeyPem { get; set; } = string.Empty;

    public string JwtJwksUrl { get; set; } = string.Empty;

    public int JwtJwksRefreshMinutes { get; set; } = 60;

    public string ReadScopeClaim { get; set; } = string.Empty;

    public int MaxDimensions { get; set; } = 64;

    public int MaxMeasures { get; set; } = 64;

    public int RetentionDays { get; set; } = 90;

    public bool BackfillRollups { get; set; } = true;

    public List<EventTypeRetentionOptions> EventTypeRetention { get; set; } = [];

    public List<ProjectKeyOptions> Projects { get; set; } = [];

    public string SchemaFile { get; set; } = string.Empty;

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

    public int ConnectionMaxIdleSeconds { get; set; } = 300;
    public int WalReplayRetryDelayMs { get; set; } = 10_000;
    public int WalFileBufferBytes { get; set; } = 65_536;
    public bool EnableWireCompression { get; set; } = true;
    public bool EnableAsyncInsert { get; set; } = true;
    public int MaxConcurrentConnections { get; set; } = 20_000;
    public bool EnableRequestDecompression { get; set; } = true;
    public bool EnablePrometheusScrape { get; set; } = false;
}

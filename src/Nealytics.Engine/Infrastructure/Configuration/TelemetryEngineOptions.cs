namespace Nealytics.Engine.Infrastructure.Configuration;

using System.Collections.Generic;

/// <summary>
/// One analytics dimension, declared by the deployment rather than by the engine.
///
/// The engine ships this list empty. A deployment names its own dimensions in configuration —
/// <c>TelemetryEngine__Dimensions__0__Name</c> and friends — which is the whole reason the engine
/// no longer contains any customer's vocabulary.
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

public sealed class MeasureOptions
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "";

    public string Aggregations { get; set; } = "sum,avg,min,max,count";

    public double? Minimum { get; set; }

    public double? Maximum { get; set; }

    public bool Retired { get; set; }
}

/// <summary>
/// Binds one project key to the project it may write to.
///
/// Optional and per key. A key with no entry keeps today's behaviour exactly, so a deployment can
/// adopt this one key at a time instead of as a migration.
///
/// <b>Only the project, never the tenant.</b> The beacon architecture legitimately writes many
/// tenants through a single key -- that is the whole point of one edge worker serving every venue --
/// so a tenant pin would be wrong by design. A project pin costs nothing and closes the case where a
/// leaked or misconfigured key writes into someone else's project.
/// </summary>
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
}

public sealed class TelemetryEngineOptions
{
    /// <summary>
    /// Dimensions this deployment declares. Empty in the shipped <c>appsettings.json</c> on
    /// purpose — see <see cref="DimensionOptions"/>.
    /// </summary>
    public List<DimensionOptions> Dimensions { get; set; } = [];

    public List<MeasureOptions> Measures { get; set; } = [];

    public List<RollupOptions> Rollups { get; set; } = [];

    /// <summary>
    /// Ceiling on declared dimensions. The registry issues DDL from this list, so a config bug
    /// must not be able to add columns without limit.
    /// </summary>
    public int MaxDimensions { get; set; } = 64;

    public int MaxMeasures { get; set; } = 64;

    public int RetentionDays { get; set; } = 90;

    public bool BackfillRollups { get; set; } = true;

    /// <summary>
    /// Event types kept for less time than <see cref="RetentionDays"/>.
    ///
    /// Every analytics deployment has one event type an order of magnitude larger than the rest,
    /// whose long-term value is nil once it has been rolled up. Impressions are the usual example.
    ///
    /// <b>Shorter only.</b> Measured on ClickHouse 26.7.1: a compound TTL evaluates every rule
    /// independently and any match deletes, so the shortest applicable rule wins and the order they
    /// are written in makes no difference. A per-type retention longer than the base therefore does
    /// nothing at all -- the rows are destroyed on the base schedule while the declaration says they
    /// are kept for a year. The boot refuses that rather than accept it.
    /// </summary>
    public List<EventTypeRetentionOptions> EventTypeRetention { get; set; } = [];

    /// <summary>
    /// Optional per-key project pinning. Empty means every valid key may write any projectId,
    /// which is what every existing deployment does today.
    /// </summary>
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

    /// <summary>
    /// How long a pooled connection may sit idle before it is reopened rather than reused.
    ///
    /// <para>Must stay below the server's <c>idle_connection_timeout</c>, which ClickHouse defaults
    /// to 3600 seconds. Past that the server closes the socket without logging anything, the
    /// connection still reports <c>Open</c>, and the first write on it fails with a broken pipe.
    /// </para>
    ///
    /// <para>Set to 0 to disable the check and reuse a pooled connection at any age.</para>
    /// </summary>
    public int ConnectionMaxIdleSeconds { get; set; } = 300;
    public int WalReplayRetryDelayMs { get; set; } = 10_000;
    public int WalFileBufferBytes { get; set; } = 65_536;
    public bool EnableWireCompression { get; set; } = true;
    public bool EnableAsyncInsert { get; set; } = true;
    public int MaxConcurrentConnections { get; set; } = 20_000;
    public bool EnableRequestDecompression { get; set; } = true;
    public bool EnablePrometheusScrape { get; set; } = false;
}

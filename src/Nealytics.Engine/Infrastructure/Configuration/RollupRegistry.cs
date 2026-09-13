namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

public enum RollupGrain
{
    Hour,
    Day,

    /// <summary>
    /// One row per session rather than per time bucket.
    ///
    /// This is what replaces a scheduled sessionizer. A job that sweeps sessions idle for thirty
    /// minutes has to be re-run for late arrivals and still leaves a hole when one lands after the
    /// re-run; an <c>AggregatingMergeTree</c> fed by a materialized view is always current and
    /// cannot have that hole, because a late row is simply another partial state that merges in.
    /// "Session ended" then needs no idle rule at all — it is <c>maxMerge(timestamp)</c>.
    /// </summary>
    Session,
}

public sealed record RollupMeasure(Measure Measure, string Aggregation, string ColumnName);

/// <summary>
/// One grouping column in a rollup: either a declared dimension or a core column.
///
/// Core columns have to be groupable here or a rollup cannot key on <c>object_id</c>, which is the
/// identity every item report is built on — the rollup would exist and be unable to answer the one
/// question it was created for.
/// </summary>
public sealed record RollupColumn(string Name, string StoredType);

public sealed record Rollup(
    string Name,
    string TableName,
    string ViewName,
    RollupGrain Grain,
    string BucketFunction,
    FrozenSet<string> EventTypes,
    IReadOnlyList<RollupColumn> Dimensions,
    IReadOnlyList<RollupMeasure> Measures)
{
    public bool CoversEventType(string? eventType) =>
        EventTypes.Count == 0 || (eventType is not null && EventTypes.Contains(eventType));

    public bool CoversColumn(string column) =>
        string.Equals(column, "event_type", StringComparison.Ordinal)
        || string.Equals(column, RollupRegistry.TrafficColumn, StringComparison.Ordinal)
        || Dimensions.Any(dimension => string.Equals(dimension.Name, column, StringComparison.Ordinal));

    public string? MeasureColumn(string measure, string aggregation) =>
        Measures.FirstOrDefault(candidate =>
            string.Equals(candidate.Measure.Name, measure, StringComparison.Ordinal)
            && string.Equals(candidate.Aggregation, aggregation, StringComparison.OrdinalIgnoreCase))
            ?.ColumnName;
}

public sealed class RollupRegistry
{
    public const string Database = "nealytics_core";
    public const string SourceTable = "global_events";
    public const string TrafficColumn = "traffic_class";
    private readonly ConcurrentDictionary<string, string> _unroutable = new(StringComparer.Ordinal);

    public static readonly FrozenSet<string> SupportedAggregations =
        FrozenSet.ToFrozenSet(["sum", "avg", "min", "max", "count"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Core columns a rollup may group by, and the non-nullable type it stores each as.
    ///
    /// AggregatingMergeTree refuses a nullable sorting key outright, so a rollup stores the
    /// normalised key — <c>ifNull(toString(x), '')</c> — which is also exactly how the breakdown
    /// endpoint renders its key. The two therefore agree by construction rather than by luck.
    /// </summary>
    private static readonly Dictionary<string, string> CoreGroupable = new(StringComparer.Ordinal)
    {
        ["object_id"] = "String",
        ["user_id"] = "String",
        ["session_id"] = "String",
        ["device_class"] = "LowCardinality(String)",
        ["os"] = "LowCardinality(String)",
        ["browser"] = "LowCardinality(String)",
        ["country"] = "LowCardinality(String)",
        ["traffic_class"] = "LowCardinality(String)",
        ["page_path"] = "String",
        ["referrer"] = "LowCardinality(String)",
    };

    private static readonly Regex NamePattern =
        new("^[a-z][a-z0-9_]{0,48}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public RollupRegistry(
        IOptions<TelemetryEngineOptions> options, DimensionRegistry dimensions, MeasureRegistry measures)
        : this(options.Value, dimensions, measures)
    {
    }

    public RollupRegistry(
        TelemetryEngineOptions options, DimensionRegistry dimensions, MeasureRegistry measures)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(measures);

        List<RollupOptions> configured = options.Rollups ?? [];
        List<Rollup> declared = new(configured.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int i = 0; i < configured.Count; i++)
        {
            RollupOptions declaration = configured[i];
            string name = declaration.Name?.Trim() ?? string.Empty;

            if (!NamePattern.IsMatch(name))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Rollups[{i}] has name '{declaration.Name}', which is not a valid "
                    + "table suffix. Names must match [a-z][a-z0-9_]{0,48}.");
            }

            if (!seen.Add(name))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Rollups[{i}] declares '{name}' a second time. Names must be unique.");
            }

            RollupGrain grain = (declaration.Grain?.Trim() ?? string.Empty).ToLowerInvariant() switch
            {
                "hour" => RollupGrain.Hour,
                "day" or "" => RollupGrain.Day,
                "session" => RollupGrain.Session,
                _ => throw new InvalidOperationException(
                    $"TelemetryEngine:Rollups[{i}] ('{name}') declares unknown grain "
                    + $"'{declaration.Grain}'. Supported: hour, day, session."),
            };

            List<RollupColumn> rollupDimensions = [];

            foreach (string column in Split(declaration.Dimensions))
            {
                if (string.Equals(column, "event_type", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"TelemetryEngine:Rollups[{i}] ('{name}') lists 'event_type', which every "
                        + "rollup already keys on. Listing it again would create the column twice.");
                }

                Dimension? dimension = dimensions.Find(column);

                if (dimension is not null)
                {
                    rollupDimensions.Add(new RollupColumn(column, RollupDimensionType(dimension)));
                    continue;
                }

                if (CoreGroupable.TryGetValue(column, out string? coreType))
                {
                    rollupDimensions.Add(new RollupColumn(column, coreType));
                    continue;
                }

                throw new InvalidOperationException(
                    $"TelemetryEngine:Rollups[{i}] ('{name}') groups by '{column}', which is neither "
                    + "an active declared dimension nor a groupable core column. A rollup over a "
                    + "column that does not exist would produce a table nothing can answer from. "
                    + $"Core columns available: {string.Join(", ", CoreGroupable.Keys.Order(StringComparer.Ordinal))}.");
            }

            List<RollupMeasure> rollupMeasures = [];

            foreach (string entry in Split(declaration.Measures))
            {
                int colon = entry.IndexOf(':', StringComparison.Ordinal);

                if (colon <= 0 || colon == entry.Length - 1)
                {
                    throw new InvalidOperationException(
                        $"TelemetryEngine:Rollups[{i}] ('{name}') declares measure entry '{entry}'. "
                        + "Each entry must be measure:aggregation, such as dwell_ms:sum.");
                }

                string measureName = entry[..colon];
                string aggregation = entry[(colon + 1)..];

                Measure? measure = measures.Find(measureName)
                    ?? throw new InvalidOperationException(
                        $"TelemetryEngine:Rollups[{i}] ('{name}') aggregates '{measureName}', which is "
                        + "not an active declared measure.");

                if (!SupportedAggregations.Contains(aggregation))
                {
                    throw new InvalidOperationException(
                        $"TelemetryEngine:Rollups[{i}] ('{name}') uses aggregation '{aggregation}', which "
                        + $"a rollup cannot store. Supported: {string.Join(", ", SupportedAggregations.Order(StringComparer.Ordinal))}. "
                        + "Percentiles stay on the raw table.");
                }

                if (!measure.Aggregations.Contains(aggregation))
                {
                    throw new InvalidOperationException(
                        $"TelemetryEngine:Rollups[{i}] ('{name}') uses aggregation '{aggregation}' on "
                        + $"measure '{measure.Name}', which does not declare it. Declared: "
                        + $"{string.Join(", ", measure.Aggregations.Order(StringComparer.Ordinal))}.");
                }

                rollupMeasures.Add(new RollupMeasure(
                    measure, aggregation.ToLowerInvariant(), $"{measure.Name}_{aggregation.ToLowerInvariant()}"));
            }

            declared.Add(new Rollup(
                name,
                $"rollup_{name}",
                $"rollup_{name}_mv",
                grain,
                grain switch
                {
                    RollupGrain.Hour => "toStartOfHour",
                    // A session rollup keys on a plain Date, because a partition key must be a
                    // function of the ORDER BY columns and you cannot partition by min(timestamp).
                    RollupGrain.Session => "toDate",
                    _ => "toStartOfDay",
                },
                FrozenSet.ToFrozenSet(Split(declaration.EventTypes), StringComparer.Ordinal),
                rollupDimensions,
                rollupMeasures));
        }

        Declared = declared;
    }

    public IReadOnlyList<Rollup> Declared { get; }

    public IEnumerable<Rollup> Routable
    {
        get
        {
            foreach (Rollup rollup in Declared)
            {
                if (!_unroutable.ContainsKey(rollup.Name))
                {
                    yield return rollup;
                }
            }
        }
    }

    public void MarkUnroutable(Rollup rollup, string reason)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        _unroutable[rollup.Name] = reason;
    }

    public void MarkRoutable(Rollup rollup)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        _unroutable.TryRemove(rollup.Name, out _);
    }

    public bool IsRoutable(Rollup rollup) =>
        rollup is not null && !_unroutable.ContainsKey(rollup.Name);

    private static string[] Split(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The key columns of a session rollup, and why they are not the time-bucketed ones.
    ///
    /// <b>A plain <c>event_date Date</c>, not the session's start.</b> A partition key must be a
    /// function of the ORDER BY columns, and <c>min(timestamp)</c> is an aggregate — there is
    /// nothing to partition by. So the date the row's events fell on is a key column, and a session
    /// crossing midnight produces two rows. The read side regroups by <c>session_id</c> and they
    /// recombine. Every daily bucketing has this property; it is honest, not a defect.
    ///
    /// <b>No <c>event_type</c>.</b> Every time-bucketed rollup keys on it, and a session rollup must
    /// not: a session touching five event types would become five rows, and its duration would be
    /// measured per event type rather than per session — a number that looks entirely reasonable and
    /// answers a question nobody asked. <c>EventTypes</c> still decides which events feed the view.
    /// </summary>
    private static bool IsSessionGrain(Rollup rollup) => rollup.Grain == RollupGrain.Session;

    public static string BuildTableDdl(Rollup rollup)
    {
        ArgumentNullException.ThrowIfNull(rollup);

        return IsSessionGrain(rollup) ? BuildSessionTableDdl(rollup) : BuildBucketTableDdl(rollup);
    }

    private static string BuildSessionTableDdl(Rollup rollup)
    {
        StringBuilder sql = new(512);
        sql.Append("CREATE TABLE IF NOT EXISTS ").Append(Database).Append('.').Append(rollup.TableName);
        sql.Append(" (project_id LowCardinality(String), tenant_id String, event_date Date, ");
        sql.Append("session_id String, ").Append(TrafficColumn).Append(" LowCardinality(String)");

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ").Append(dimension.Name).Append(' ').Append(dimension.StoredType);
        }

        sql.Append(", events AggregateFunction(count)");

        // The two facts a scheduled sessionizer exists to compute, held as states so a late event
        // simply merges in rather than arriving after the job that would have counted it.
        sql.Append(", started_at AggregateFunction(min, DateTime64(3, 'UTC'))");
        sql.Append(", ended_at AggregateFunction(max, DateTime64(3, 'UTC'))");
        sql.Append(", users AggregateFunction(uniqExact, Nullable(String))");
        sql.Append(", event_types AggregateFunction(uniqExact, String)");

        foreach (RollupMeasure measure in rollup.Measures)
        {
            sql.Append(", ").Append(measure.ColumnName)
                .Append(" AggregateFunction(").Append(measure.Aggregation).Append(", ")
                .Append(measure.Measure.ClickHouseType).Append(')');
        }

        sql.Append(") ENGINE = AggregatingMergeTree PARTITION BY toYYYYMM(event_date) ");
        sql.Append("ORDER BY (project_id, tenant_id, event_date, session_id, ").Append(TrafficColumn);

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ").Append(dimension.Name);
        }

        sql.Append(')');
        return sql.ToString();
    }

    private static string BuildBucketTableDdl(Rollup rollup)
    {
        StringBuilder sql = new(512);
        sql.Append("CREATE TABLE IF NOT EXISTS ").Append(Database).Append('.').Append(rollup.TableName);
        sql.Append(" (project_id LowCardinality(String), tenant_id String, bucket DateTime('UTC'), ");
        sql.Append("event_type LowCardinality(String), ").Append(TrafficColumn).Append(" LowCardinality(String)");

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ").Append(dimension.Name).Append(' ').Append(dimension.StoredType);
        }

        sql.Append(", events AggregateFunction(count)");
        sql.Append(", sessions AggregateFunction(uniqExact, String)");
        sql.Append(", users AggregateFunction(uniqExact, Nullable(String))");

        foreach (RollupMeasure measure in rollup.Measures)
        {
            sql.Append(", ").Append(measure.ColumnName)
                .Append(" AggregateFunction(").Append(measure.Aggregation).Append(", ")
                .Append(measure.Measure.ClickHouseType).Append(')');
        }

        sql.Append(") ENGINE = AggregatingMergeTree PARTITION BY toYYYYMM(bucket) ");
        sql.Append("ORDER BY (project_id, tenant_id, bucket, event_type, ").Append(TrafficColumn);

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ").Append(dimension.Name);
        }

        sql.Append(')');
        return sql.ToString();
    }

    public static string BuildViewDdl(Rollup rollup)
    {
        ArgumentNullException.ThrowIfNull(rollup);

        return IsSessionGrain(rollup) ? BuildSessionViewDdl(rollup) : BuildBucketViewDdl(rollup);
    }

    private static string BuildSessionViewDdl(Rollup rollup)
    {
        StringBuilder sql = new(768);
        sql.Append("CREATE MATERIALIZED VIEW ").Append(Database).Append('.').Append(rollup.ViewName);
        sql.Append(" TO ").Append(Database).Append('.').Append(rollup.TableName);
        sql.Append(" AS ");
        AppendSessionSelect(sql, rollup, null);
        return sql.ToString();
    }

    private static void AppendSessionSelect(StringBuilder sql, Rollup rollup, string? partition)
    {
        sql.Append("SELECT project_id, tenant_id, toDate(timestamp) AS event_date, session_id, ").Append(TrafficColumn);

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ifNull(toString(").Append(dimension.Name).Append("), '') AS ").Append(dimension.Name);
        }

        sql.Append(", countState() AS events");
        sql.Append(", minState(timestamp) AS started_at");
        sql.Append(", maxState(timestamp) AS ended_at");
        sql.Append(", uniqExactState(user_id) AS users");

        // Kept as a state rather than frozen into an is_bounce flag at write time. A bounce is
        // "one event type touched", and deciding that here would bake one product's threshold into
        // storage; countMerge and uniqExactMerge answer it at read time, and can answer a different
        // definition tomorrow without rebuilding the table.
        sql.Append(", uniqExactState(event_type) AS event_types");

        foreach (RollupMeasure measure in rollup.Measures)
        {
            sql.Append(", ").Append(measure.Aggregation).Append("State(")
                .Append(measure.Measure.Name).Append(") AS ").Append(measure.ColumnName);
        }

        AppendSourceAndScope(sql, rollup, partition);
        sql.Append(" GROUP BY project_id, tenant_id, event_date, session_id, ").Append(TrafficColumn);

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ").Append(dimension.Name);
        }
    }

    private static string BuildBucketViewDdl(Rollup rollup)
    {
        StringBuilder sql = new(768);
        sql.Append("CREATE MATERIALIZED VIEW ").Append(Database).Append('.').Append(rollup.ViewName);
        sql.Append(" TO ").Append(Database).Append('.').Append(rollup.TableName);
        sql.Append(" AS ");
        AppendBucketSelect(sql, rollup, null);
        return sql.ToString();
    }

    private static void AppendBucketSelect(StringBuilder sql, Rollup rollup, string? partition)
    {
        sql.Append("SELECT project_id, tenant_id, ");
        sql.Append(rollup.BucketFunction).Append("(timestamp) AS bucket, event_type, ").Append(TrafficColumn);

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ifNull(toString(").Append(dimension.Name).Append("), '') AS ").Append(dimension.Name);
        }

        sql.Append(", countState() AS events");
        sql.Append(", uniqExactState(session_id) AS sessions");
        sql.Append(", uniqExactState(user_id) AS users");

        foreach (RollupMeasure measure in rollup.Measures)
        {
            sql.Append(", ").Append(measure.Aggregation).Append("State(")
                .Append(measure.Measure.Name).Append(") AS ").Append(measure.ColumnName);
        }

        AppendSourceAndScope(sql, rollup, partition);
        sql.Append(" GROUP BY project_id, tenant_id, bucket, event_type, ").Append(TrafficColumn);

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ").Append(dimension.Name);
        }
    }

    private static void AppendSourceAndScope(StringBuilder sql, Rollup rollup, string? partition)
    {
        sql.Append(" FROM ").Append(Database).Append('.').Append(SourceTable);
        bool scoped = false;

        if (partition is not null)
        {
            sql.Append(" WHERE toYYYYMM(timestamp) = ").Append(partition);
            scoped = true;
        }

        if (rollup.EventTypes.Count > 0)
        {
            sql.Append(scoped ? " AND " : " WHERE ");
            sql.Append("event_type IN (");
            sql.Append(string.Join(", ", rollup.EventTypes.Order(StringComparer.Ordinal).Select(Quote)));
            sql.Append(')');
        }
    }

    public static string BuildBackfillSql(Rollup rollup, string? partition)
    {
        ArgumentNullException.ThrowIfNull(rollup);

        if (partition is not null && !IsPartitionId(partition))
        {
            throw new ArgumentException($"'{partition}' is not a toYYYYMM partition id.", nameof(partition));
        }

        StringBuilder sql = new(768);
        sql.Append("INSERT INTO ").Append(Database).Append('.').Append(rollup.TableName).Append(' ');

        if (IsSessionGrain(rollup))
        {
            AppendSessionSelect(sql, rollup, partition);
        }
        else
        {
            AppendBucketSelect(sql, rollup, partition);
        }

        return sql.ToString();
    }

    public static bool IsPartitionId(string value)
    {
        if (value.Length != 6)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    internal static string RollupDimensionType(Dimension dimension) =>
        dimension.Kind == DimensionValueKind.LowCardinalityString
            ? "LowCardinality(String)"
            : "String";

    private static string Quote(string value) =>
        "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                   .Replace("'", "\\'", StringComparison.Ordinal) + "'";
}

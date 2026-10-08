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

    Session,
}

public sealed record RollupMeasure(Measure Measure, string Aggregation, string ColumnName);

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
    public IReadOnlyList<string> TenantAttributes { get; init; } = [];

    public bool IsGroupRollup => TenantAttributes.Count > 0;

    public bool CoversEventType(string? eventType) =>
        EventTypes.Count == 0 || (eventType is not null && EventTypes.Contains(eventType));

    public bool CoversColumn(string column) =>
        string.Equals(column, "event_type", StringComparison.Ordinal)
        || string.Equals(column, RollupRegistry.TrafficColumn, StringComparison.Ordinal)
        || Dimensions.Any(dimension => string.Equals(dimension.Name, column, StringComparison.Ordinal));

    public bool CoversTenantColumn(string column)
    {
        if (string.Equals(column, "tenant_id", StringComparison.Ordinal))
        {
            return !IsGroupRollup;
        }

        if (!TenantAttributeRegistry.IsGroupColumn(column))
        {
            return false;
        }

        return !IsGroupRollup || HoldsTenantAttribute(TenantAttributeRegistry.AttributeOf(column));
    }

    public bool HoldsTenantAttribute(string attribute)
    {
        foreach (string held in TenantAttributes)
        {
            if (string.Equals(held, attribute, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public string? MeasureColumn(string measure, string aggregation) =>
        Measures.FirstOrDefault(candidate =>
            string.Equals(candidate.Measure.Name, measure, StringComparison.Ordinal)
            && string.Equals(candidate.Aggregation, aggregation, StringComparison.OrdinalIgnoreCase))
            ?.ColumnName;

    public string? MergeExpression(string measure, string aggregation, string? condition)
    {
        string? column = MeasureColumn(measure, aggregation);

        if (column is null)
        {
            return null;
        }

        string canonical = aggregation.ToLowerInvariant();

        if (RollupRegistry.QuantileIndex(canonical) is int index)
        {
            string merged = condition is null
                ? $"quantilesMerge({RollupRegistry.QuantileLevels})({column})"
                : $"quantilesMergeIf({RollupRegistry.QuantileLevels})({column}, {condition})";
            return $"arrayElement({merged}, {index.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
        }

        return condition is null
            ? $"{canonical}Merge({column})"
            : $"{canonical}MergeIf({column}, {condition})";
    }
}

public sealed class RollupRegistry
{
    public const string Database = "nealytics_core";
    public const string SourceTable = "global_events";
    public const string TrafficColumn = "traffic_class";
    private readonly ConcurrentDictionary<string, string> _unroutable = new(StringComparer.Ordinal);

    public const string QuantileLevels = "0.5, 0.75, 0.9, 0.95, 0.99";
    public const string TenantAttributeColumnPrefix = "tenant_attr_";

    public static readonly FrozenSet<string> SupportedAggregations =
        FrozenSet.ToFrozenSet(
            ["sum", "avg", "min", "max", "count", "p50", "p75", "p90", "p95", "p99"],
            StringComparer.OrdinalIgnoreCase);

    public static int? QuantileIndex(string aggregation) => aggregation.ToLowerInvariant() switch
    {
        "p50" => 1,
        "p75" => 2,
        "p90" => 3,
        "p95" => 4,
        "p99" => 5,
        _ => null,
    };

    public static bool IsQuantile(string aggregation) => QuantileIndex(aggregation) is not null;

    public static string TenantAttributeColumn(string attribute) => TenantAttributeColumnPrefix + attribute;

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
        IOptions<TelemetryEngineOptions> options,
        DimensionRegistry dimensions,
        MeasureRegistry measures,
        TenantAttributeRegistry tenantAttributes)
        : this(options.Value, dimensions, measures, tenantAttributes)
    {
    }

    public RollupRegistry(
        TelemetryEngineOptions options,
        DimensionRegistry dimensions,
        MeasureRegistry measures,
        TenantAttributeRegistry? tenantAttributes = null)
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
                        + $"a rollup cannot store. Supported: {string.Join(", ", SupportedAggregations.Order(StringComparer.Ordinal))}.");
                }

                if (!measure.Aggregations.Contains(aggregation))
                {
                    throw new InvalidOperationException(
                        $"TelemetryEngine:Rollups[{i}] ('{name}') uses aggregation '{aggregation}' on "
                        + $"measure '{measure.Name}', which does not declare it. Declared: "
                        + $"{string.Join(", ", measure.Aggregations.Order(StringComparer.Ordinal))}.");
                }

                string canonical = aggregation.ToLowerInvariant();

                rollupMeasures.Add(new RollupMeasure(
                    measure,
                    canonical,
                    IsQuantile(canonical) ? $"{measure.Name}_quantiles" : $"{measure.Name}_{canonical}"));
            }

            List<string> rollupAttributes = [];

            foreach (string attribute in Split(declaration.TenantAttributes))
            {
                if (tenantAttributes is null || !tenantAttributes.IsDeclared(attribute))
                {
                    throw new InvalidOperationException(
                        $"TelemetryEngine:Rollups[{i}] ('{name}') groups tenants by '{attribute}', which is "
                        + "not declared under TelemetryEngine:TenantAttributes.");
                }

                if (rollupAttributes.Contains(attribute))
                {
                    throw new InvalidOperationException(
                        $"TelemetryEngine:Rollups[{i}] ('{name}') lists tenant attribute '{attribute}' twice.");
                }

                if (grain == RollupGrain.Session)
                {
                    throw new InvalidOperationException(
                        $"TelemetryEngine:Rollups[{i}] ('{name}') is a session rollup and cannot group "
                        + "tenants by an attribute. Use an hour or day grain.");
                }

                string column = TenantAttributeColumn(attribute);

                if (rollupDimensions.Any(dimension => string.Equals(dimension.Name, column, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"TelemetryEngine:Rollups[{i}] ('{name}') groups by dimension '{column}', which is "
                        + "the column name a tenant attribute rollup reserves.");
                }

                rollupAttributes.Add(attribute);
            }

            declared.Add(new Rollup(
                name,
                $"rollup_{name}",
                $"rollup_{name}_mv",
                grain,
                grain switch
                {
                    RollupGrain.Hour => "toStartOfHour",
                    RollupGrain.Session => "toDate",
                    _ => "toStartOfDay",
                },
                FrozenSet.ToFrozenSet(Split(declaration.EventTypes), StringComparer.Ordinal),
                rollupDimensions,
                rollupMeasures)
            {
                TenantAttributes = rollupAttributes,
            });
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

    private static bool IsSessionGrain(Rollup rollup) => rollup.Grain == RollupGrain.Session;

    public static string BuildTableDdl(Rollup rollup, string onCluster = "")
    {
        ArgumentNullException.ThrowIfNull(rollup);

        return IsSessionGrain(rollup) ? BuildSessionTableDdl(rollup, onCluster) : BuildBucketTableDdl(rollup, onCluster);
    }

    private static IEnumerable<RollupMeasure> StoredMeasures(Rollup rollup)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (RollupMeasure measure in rollup.Measures)
        {
            if (seen.Add(measure.ColumnName))
            {
                yield return measure;
            }
        }
    }

    private static void AppendMeasureColumns(StringBuilder sql, Rollup rollup)
    {
        foreach (RollupMeasure measure in StoredMeasures(rollup))
        {
            sql.Append(", ").Append(measure.ColumnName).Append(" AggregateFunction(");

            if (IsQuantile(measure.Aggregation))
            {
                sql.Append("quantiles(").Append(QuantileLevels).Append(')');
            }
            else
            {
                sql.Append(measure.Aggregation);
            }

            sql.Append(", ").Append(measure.Measure.ClickHouseType).Append(')');
        }
    }

    private static void AppendMeasureStates(StringBuilder sql, Rollup rollup, string source)
    {
        foreach (RollupMeasure measure in StoredMeasures(rollup))
        {
            sql.Append(", ");

            if (IsQuantile(measure.Aggregation))
            {
                sql.Append("quantilesState(").Append(QuantileLevels).Append(")(");
            }
            else
            {
                sql.Append(measure.Aggregation).Append("State(");
            }

            sql.Append(source).Append(measure.Measure.Name).Append(") AS ").Append(measure.ColumnName);
        }
    }

    private static string BuildSessionTableDdl(Rollup rollup, string onCluster)
    {
        StringBuilder sql = new(512);
        sql.Append("CREATE TABLE IF NOT EXISTS ").Append(Database).Append('.').Append(rollup.TableName).Append(onCluster);
        sql.Append(" (project_id LowCardinality(String), tenant_id String, event_date Date, ");
        sql.Append("session_id String, ").Append(TrafficColumn).Append(" LowCardinality(String)");

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ").Append(dimension.Name).Append(' ').Append(dimension.StoredType);
        }

        sql.Append(", events AggregateFunction(count)");

        sql.Append(", started_at AggregateFunction(min, DateTime64(3, 'UTC'))");
        sql.Append(", ended_at AggregateFunction(max, DateTime64(3, 'UTC'))");
        sql.Append(", users AggregateFunction(uniqExact, Nullable(String))");
        sql.Append(", event_types AggregateFunction(uniqExact, String)");

        AppendMeasureColumns(sql, rollup);

        sql.Append(") ENGINE = AggregatingMergeTree PARTITION BY toYYYYMM(event_date) ");
        sql.Append("ORDER BY (project_id, tenant_id, event_date, session_id, ").Append(TrafficColumn);

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ").Append(dimension.Name);
        }

        sql.Append(')');
        return sql.ToString();
    }

    private static string BuildBucketTableDdl(Rollup rollup, string onCluster)
    {
        StringBuilder sql = new(512);
        sql.Append("CREATE TABLE IF NOT EXISTS ").Append(Database).Append('.').Append(rollup.TableName).Append(onCluster);
        sql.Append(" (project_id LowCardinality(String), ");

        if (rollup.IsGroupRollup)
        {
            foreach (string attribute in rollup.TenantAttributes)
            {
                sql.Append(TenantAttributeColumn(attribute)).Append(" String, ");
            }
        }
        else
        {
            sql.Append("tenant_id String, ");
        }

        sql.Append("bucket DateTime('UTC'), ");
        sql.Append("event_type LowCardinality(String), ").Append(TrafficColumn).Append(" LowCardinality(String)");

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ").Append(dimension.Name).Append(' ').Append(dimension.StoredType);
        }

        sql.Append(", events AggregateFunction(count)");
        sql.Append(", sessions AggregateFunction(uniqExact, String)");
        sql.Append(", users AggregateFunction(uniqExact, Nullable(String))");

        AppendMeasureColumns(sql, rollup);

        sql.Append(") ENGINE = AggregatingMergeTree PARTITION BY toYYYYMM(bucket) ");
        sql.Append("ORDER BY (project_id, ").Append(TenantKey(rollup)).Append(", bucket, event_type, ").Append(TrafficColumn);

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ").Append(dimension.Name);
        }

        sql.Append(')');
        return sql.ToString();
    }

    private static string TenantKey(Rollup rollup) =>
        rollup.IsGroupRollup
            ? string.Join(", ", rollup.TenantAttributes.Select(TenantAttributeColumn))
            : "tenant_id";

    public static string BuildViewDdl(Rollup rollup, string onCluster = "")
    {
        ArgumentNullException.ThrowIfNull(rollup);

        return IsSessionGrain(rollup) ? BuildSessionViewDdl(rollup, onCluster) : BuildBucketViewDdl(rollup, onCluster);
    }

    private static string BuildSessionViewDdl(Rollup rollup, string onCluster)
    {
        StringBuilder sql = new(768);
        sql.Append("CREATE MATERIALIZED VIEW ").Append(Database).Append('.').Append(rollup.ViewName).Append(onCluster);
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

        sql.Append(", uniqExactState(event_type) AS event_types");

        AppendMeasureStates(sql, rollup, string.Empty);

        AppendSourceAndScope(sql, rollup, partition);
        sql.Append(" GROUP BY project_id, tenant_id, event_date, session_id, ").Append(TrafficColumn);

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ").Append(dimension.Name);
        }
    }

    private static string BuildBucketViewDdl(Rollup rollup, string onCluster)
    {
        StringBuilder sql = new(768);
        sql.Append("CREATE MATERIALIZED VIEW ").Append(Database).Append('.').Append(rollup.ViewName).Append(onCluster);
        sql.Append(" TO ").Append(Database).Append('.').Append(rollup.TableName);
        sql.Append(" AS ");
        AppendBucketSelect(sql, rollup, null);
        return sql.ToString();
    }

    private static void AppendBucketSelect(StringBuilder sql, Rollup rollup, string? partition)
    {
        if (rollup.IsGroupRollup)
        {
            AppendGroupSelect(sql, rollup, partition);
            return;
        }

        sql.Append("SELECT project_id, tenant_id, ");
        sql.Append(rollup.BucketFunction).Append("(timestamp) AS bucket, event_type, ").Append(TrafficColumn);

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ifNull(toString(").Append(dimension.Name).Append("), '') AS ").Append(dimension.Name);
        }

        sql.Append(", countState() AS events");
        sql.Append(", uniqExactState(session_id) AS sessions");
        sql.Append(", uniqExactState(user_id) AS users");

        AppendMeasureStates(sql, rollup, string.Empty);

        AppendSourceAndScope(sql, rollup, partition);
        sql.Append(" GROUP BY project_id, tenant_id, bucket, event_type, ").Append(TrafficColumn);

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ").Append(dimension.Name);
        }
    }

    private static void AppendGroupSelect(StringBuilder sql, Rollup rollup, string? partition)
    {
        sql.Append("SELECT source.project_id AS project_id");

        foreach (string attribute in rollup.TenantAttributes)
        {
            string column = TenantAttributeColumn(attribute);
            sql.Append(", attributes.").Append(column).Append(" AS ").Append(column);
        }

        sql.Append(", ").Append(rollup.BucketFunction).Append("(source.timestamp) AS bucket");
        sql.Append(", source.event_type AS event_type, source.").Append(TrafficColumn).Append(" AS ").Append(TrafficColumn);

        foreach (RollupColumn dimension in rollup.Dimensions)
        {
            sql.Append(", ifNull(toString(source.").Append(dimension.Name).Append("), '') AS ").Append(dimension.Name);
        }

        sql.Append(", countState() AS events");
        sql.Append(", uniqExactState(source.session_id) AS sessions");
        sql.Append(", uniqExactState(source.user_id) AS users");

        AppendMeasureStates(sql, rollup, "source.");

        sql.Append(" FROM (SELECT * FROM ").Append(Database).Append('.').Append(SourceTable);
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

        sql.Append(") AS source LEFT ANY JOIN (SELECT project_id, tenant_id");

        foreach (string attribute in rollup.TenantAttributes)
        {
            sql.Append(", anyIf(value, attribute = ").Append(Quote(attribute)).Append(") AS ")
                .Append(TenantAttributeColumn(attribute));
        }

        sql.Append(" FROM ").Append(Database).Append('.').Append(TenantAttributeRegistry.Table);
        sql.Append(" FINAL WHERE attribute IN (");
        sql.Append(string.Join(", ", rollup.TenantAttributes.Select(Quote)));
        sql.Append(") GROUP BY project_id, tenant_id) AS attributes");
        sql.Append(" ON source.project_id = attributes.project_id AND source.tenant_id = attributes.tenant_id");
        sql.Append(" GROUP BY project_id");

        foreach (string attribute in rollup.TenantAttributes)
        {
            sql.Append(", ").Append(TenantAttributeColumn(attribute));
        }

        sql.Append(", bucket, event_type, ").Append(TrafficColumn);

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

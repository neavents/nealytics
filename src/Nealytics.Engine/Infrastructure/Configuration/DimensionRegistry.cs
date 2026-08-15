namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

/// <summary>
/// How a dimension's value is carried in a column buffer. Derived from the declared type once, so
/// the buffer factory switches on a closed enum rather than re-parsing a config string.
/// </summary>
public enum DimensionValueKind
{
    String,
    LowCardinalityString,
    UInt64,
    Int64,
    DateTime,
}

/// <summary>One declared dimension, resolved from config to a concrete ClickHouse column.</summary>
public sealed record Dimension(
    string Name,
    string ConfiguredType,
    string ClickHouseType,
    string? DefaultExpression,
    DimensionValueKind Kind,
    bool Retired);

/// <summary>
/// The single source of truth for which dimensions exist and what they are called.
///
/// Ingestion validation, the schema reconciler and the query allowlist all read from this one
/// object. Three copies of "which dimensions exist" is how they drift: two queries over the same
/// concept fall out of step, each stays internally consistent, and the disagreement only shows up
/// as missing rows nobody can attribute to a cause.
///
/// Built once at boot. An invalid declaration throws here, which refuses the boot, because every
/// alternative is worse: a dimension named <c>timestamp</c> generates DDL that either fails
/// obscurely or shadows a core column, and a duplicate name produces two buffers writing to one
/// column.
/// </summary>
public sealed class DimensionRegistry
{
    /// <summary>
    /// Core columns a dimension may not shadow.
    ///
    /// <c>metadata_json</c> is on this list even though it is scheduled for removal: while it
    /// exists, a dimension of that name would generate an ADD COLUMN that silently no-ops and then
    /// write strings into a column the engine also writes from another source.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ReservedColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        "event_id",
        "project_id",
        "tenant_id",
        "session_id",
        "user_id",
        "event_type",
        "object_id",
        "seq",
        "traffic_class",
        "page_path",
        "referrer",
        "ingested_at",
        "device_class",
        "os",
        "browser",
        "country",
        "metadata_json",
        "timestamp",
    };

    /// <summary>
    /// Configured type name to the exact string ClickHouse reports in <c>system.columns.type</c>.
    ///
    /// These are not guesses — each was read back from a probe table on ClickHouse 26.7.1. The
    /// reconciler compares the declared type against this column verbatim, so a value that merely
    /// looks right would turn every boot into a spurious type-mismatch refusal.
    /// </summary>
    private static readonly Dictionary<string, (string ClickHouseType, string? Default, DimensionValueKind Kind)> SupportedTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["String"] = ("Nullable(String)", null, DimensionValueKind.String),
            ["LowCardinality"] = ("LowCardinality(String)", "''", DimensionValueKind.LowCardinalityString),
            ["UInt64"] = ("Nullable(UInt64)", null, DimensionValueKind.UInt64),
            ["Int64"] = ("Nullable(Int64)", null, DimensionValueKind.Int64),
            ["DateTime"] = ("Nullable(DateTime64(3, 'UTC'))", null, DimensionValueKind.DateTime),
        };

    private static readonly Regex NamePattern =
        new("^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, Dimension> _active;
    private readonly Dictionary<string, Dimension> _declared;

    public DimensionRegistry(IOptions<TelemetryEngineOptions> options)
        : this(options.Value)
    {
    }

    public DimensionRegistry(TelemetryEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<DimensionOptions> configured = options.Dimensions ?? [];

        if (options.MaxDimensions <= 0)
        {
            throw new InvalidOperationException(
                $"TelemetryEngine:MaxDimensions must be positive, but was {options.MaxDimensions}.");
        }

        if (configured.Count > options.MaxDimensions)
        {
            throw new InvalidOperationException(
                $"TelemetryEngine:Dimensions declares {configured.Count} dimensions, "
                + $"exceeding MaxDimensions of {options.MaxDimensions}.");
        }

        List<Dimension> ordered = new(configured.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int i = 0; i < configured.Count; i++)
        {
            DimensionOptions declaration = configured[i];
            string name = declaration.Name?.Trim() ?? string.Empty;

            if (!NamePattern.IsMatch(name))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Dimensions[{i}] has name '{declaration.Name}', which is not a valid "
                    + "column name. Names must match [a-z][a-z0-9_]{0,62}.");
            }

            if (ReservedColumns.Contains(name))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Dimensions[{i}] declares '{name}', which is a reserved core column. "
                    + "Pick another name.");
            }

            if (!seen.Add(name))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Dimensions[{i}] declares '{name}' a second time. Names must be unique.");
            }

            string type = declaration.Type?.Trim() ?? string.Empty;

            if (!SupportedTypes.TryGetValue(type, out (string ClickHouseType, string? Default, DimensionValueKind Kind) resolved))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Dimensions[{i}] ('{name}') declares unknown type '{declaration.Type}'. "
                    + $"Supported types: {string.Join(", ", SupportedTypes.Keys.OrderBy(k => k, StringComparer.Ordinal))}.");
            }

            ordered.Add(new Dimension(
                name,
                type,
                resolved.ClickHouseType,
                resolved.Default,
                resolved.Kind,
                declaration.Retired));
        }

        Declared = ordered;
        Active = ordered.Where(d => !d.Retired).ToArray();

        _declared = ordered.ToDictionary(d => d.Name, StringComparer.Ordinal);
        _active = Active.ToDictionary(d => d.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every dimension the deployment declared, retired ones included, in config order.
    ///
    /// The reconciler needs the retired ones: a retired column still exists and still holds data,
    /// and treating it as undeclared would refuse the boot it was meant to permit.
    /// </summary>
    public IReadOnlyList<Dimension> Declared { get; }

    /// <summary>Declared and not retired, in config order. Config order is the insert order.</summary>
    public IReadOnlyList<Dimension> Active { get; }

    public bool IsActive(string name) => _active.ContainsKey(name);

    public bool IsDeclared(string name) => _declared.ContainsKey(name);

    public string? ClickHouseType(string name) =>
        _active.TryGetValue(name, out Dimension? dimension) ? dimension.ClickHouseType : null;

    public Dimension? Find(string name) =>
        _active.TryGetValue(name, out Dimension? dimension) ? dimension : null;
}

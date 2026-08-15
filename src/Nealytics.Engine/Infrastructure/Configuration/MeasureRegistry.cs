namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

public enum MeasureValueKind
{
    UInt8,
    UInt16,
    UInt32,
    UInt64,
    Int16,
    Int32,
    Int64,
    Float32,
    Float64,
    Decimal,
}

public sealed record Measure(
    string Name,
    string ConfiguredType,
    string ClickHouseType,
    MeasureValueKind Kind,
    FrozenSet<string> Aggregations,
    double? Minimum,
    double? Maximum,
    bool Retired);

public sealed class MeasureRegistry
{
    public static readonly FrozenSet<string> SupportedAggregations =
        FrozenSet.ToFrozenSet(
            ["sum", "avg", "min", "max", "count", "p50", "p75", "p90", "p95", "p99"],
            StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, (string ClickHouseType, MeasureValueKind Kind)> SupportedTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["UInt8"] = ("Nullable(UInt8)", MeasureValueKind.UInt8),
            ["UInt16"] = ("Nullable(UInt16)", MeasureValueKind.UInt16),
            ["UInt32"] = ("Nullable(UInt32)", MeasureValueKind.UInt32),
            ["UInt64"] = ("Nullable(UInt64)", MeasureValueKind.UInt64),
            ["Int16"] = ("Nullable(Int16)", MeasureValueKind.Int16),
            ["Int32"] = ("Nullable(Int32)", MeasureValueKind.Int32),
            ["Int64"] = ("Nullable(Int64)", MeasureValueKind.Int64),
            ["Float32"] = ("Nullable(Float32)", MeasureValueKind.Float32),
            ["Float64"] = ("Nullable(Float64)", MeasureValueKind.Float64),
            ["Decimal"] = ("Nullable(Decimal(18, 4))", MeasureValueKind.Decimal),
        };

    private static readonly Dictionary<string, string> AggregationFunctions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sum"] = "sum",
            ["avg"] = "avg",
            ["min"] = "min",
            ["max"] = "max",
            ["count"] = "count",
            ["p50"] = "quantile(0.5)",
            ["p75"] = "quantile(0.75)",
            ["p90"] = "quantile(0.9)",
            ["p95"] = "quantile(0.95)",
            ["p99"] = "quantile(0.99)",
        };

    private static readonly Regex NamePattern =
        new("^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, Measure> _active;
    private readonly Dictionary<string, Measure> _declared;

    public MeasureRegistry(IOptions<TelemetryEngineOptions> options, DimensionRegistry dimensions)
        : this(options.Value, dimensions)
    {
    }

    public MeasureRegistry(TelemetryEngineOptions options, DimensionRegistry dimensions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dimensions);

        List<MeasureOptions> configured = options.Measures ?? [];

        if (options.MaxMeasures <= 0)
        {
            throw new InvalidOperationException(
                $"TelemetryEngine:MaxMeasures must be positive, but was {options.MaxMeasures}.");
        }

        if (configured.Count > options.MaxMeasures)
        {
            throw new InvalidOperationException(
                $"TelemetryEngine:Measures declares {configured.Count} measures, "
                + $"exceeding MaxMeasures of {options.MaxMeasures}.");
        }

        List<Measure> ordered = new(configured.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int i = 0; i < configured.Count; i++)
        {
            MeasureOptions declaration = configured[i];
            string name = declaration.Name?.Trim() ?? string.Empty;

            if (!NamePattern.IsMatch(name))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Measures[{i}] has name '{declaration.Name}', which is not a valid "
                    + "column name. Names must match [a-z][a-z0-9_]{0,62}.");
            }

            if (DimensionRegistry.ReservedColumns.Contains(name))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Measures[{i}] declares '{name}', which is a reserved core column. "
                    + "Pick another name.");
            }

            if (dimensions.IsDeclared(name))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Measures[{i}] declares '{name}', which is already declared under "
                    + "TelemetryEngine:Dimensions. One name is one column, and a name cannot be both a "
                    + "group-by key and an aggregand.");
            }

            if (!seen.Add(name))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Measures[{i}] declares '{name}' a second time. Names must be unique.");
            }

            string type = declaration.Type?.Trim() ?? string.Empty;

            if (!SupportedTypes.TryGetValue(type, out (string ClickHouseType, MeasureValueKind Kind) resolved))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Measures[{i}] ('{name}') declares unknown type '{declaration.Type}'. "
                    + $"Supported types: {string.Join(", ", SupportedTypes.Keys.OrderBy(k => k, StringComparer.Ordinal))}.");
            }

            string[] aggregations = (declaration.Aggregations ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (aggregations.Length == 0)
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Measures[{i}] ('{name}') declares no aggregations. A measure no "
                    + "aggregation is allowed on can be written but never read, which is a column that "
                    + "silently collects data nothing can ask for.");
            }

            foreach (string aggregation in aggregations)
            {
                if (!SupportedAggregations.Contains(aggregation))
                {
                    throw new InvalidOperationException(
                        $"TelemetryEngine:Measures[{i}] ('{name}') declares unknown aggregation "
                        + $"'{aggregation}'. Supported: {string.Join(", ", SupportedAggregations.Order(StringComparer.Ordinal))}.");
                }
            }

            if (declaration.Minimum is double minimum
                && declaration.Maximum is double maximum
                && minimum > maximum)
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Measures[{i}] ('{name}') declares Minimum {minimum} above Maximum "
                    + $"{maximum}, which rejects every value it will ever receive.");
            }

            ordered.Add(new Measure(
                name,
                type,
                resolved.ClickHouseType,
                resolved.Kind,
                FrozenSet.ToFrozenSet(aggregations, StringComparer.OrdinalIgnoreCase),
                declaration.Minimum,
                declaration.Maximum,
                declaration.Retired));
        }

        Declared = ordered;
        Active = ordered.Where(measure => !measure.Retired).ToArray();

        _declared = ordered.ToDictionary(measure => measure.Name, StringComparer.Ordinal);
        _active = Active.ToDictionary(measure => measure.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<Measure> Declared { get; }

    public IReadOnlyList<Measure> Active { get; }

    public bool IsActive(string name) => _active.ContainsKey(name);

    public bool IsDeclared(string name) => _declared.ContainsKey(name);

    public string? ClickHouseType(string name) =>
        _active.TryGetValue(name, out Measure? measure) ? measure.ClickHouseType : null;

    public Measure? Find(string name) =>
        _active.TryGetValue(name, out Measure? measure) ? measure : null;

    public bool TryResolveAggregation(
        string? requested, Measure measure, out string aggregationFunction, out string canonicalName)
    {
        aggregationFunction = string.Empty;
        canonicalName = string.Empty;

        if (requested is null || !measure.Aggregations.TryGetValue(requested, out string? declared))
        {
            return false;
        }

        if (!AggregationFunctions.TryGetValue(declared, out string? function))
        {
            return false;
        }

        aggregationFunction = function;
        canonicalName = declared;
        return true;
    }
}

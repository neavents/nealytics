namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Nealytics.Engine.Infrastructure.Query;

public sealed class ComputedDimensionRegistry
{
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled);
    private readonly FrozenDictionary<string, ComputedDimension> _byName;

    public ComputedDimensionRegistry(TelemetryEngineOptions options, DimensionRegistry dimensions, MeasureRegistry measures)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(measures);
        if (options.MaxComputedDimensions is < 1 or > 128)
        {
            throw new InvalidOperationException("MaxComputedDimensions must be between 1 and 128.");
        }
        List<ComputedDimensionOptions> configured = options.ComputedDimensions ?? [];
        if (configured.Count > options.MaxComputedDimensions)
        {
            throw new InvalidOperationException("ComputedDimensions exceeds MaxComputedDimensions.");
        }
        QueryColumns physical = new(dimensions);
        Dictionary<string, ComputedDimensionOptions> declarations = new(StringComparer.Ordinal);
        foreach (ComputedDimensionOptions declaration in configured)
        {
            string? name = declaration?.Name?.Trim();
            if (name is null || !NamePattern.IsMatch(name) || DimensionRegistry.ReservedColumns.Contains(name)
                || dimensions.IsDeclared(name) || measures.IsDeclared(name)
                || !declarations.TryAdd(name, declaration!))
            {
                throw new InvalidOperationException("Computed dimension names must be unique, strict and distinct from physical fields.");
            }
        }
        Dictionary<string, ComputedDimension> resolved = new(StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal);
        (string Expression, bool Numeric, int Depth) ResolveSource(string? name)
        {
            if (name is not null && declarations.ContainsKey(name))
            {
                ComputedDimension dependency = Resolve(name);
                return (dependency.Expression, dependency.Numeric, dependency.Depth + 1);
            }
            if (name is not null && measures.Find(name) is Measure measure)
            {
                return (measure.Name, true, 1);
            }
            if (!physical.TryResolve(name, out string column))
            {
                throw new InvalidOperationException("Computed dimension source is not an active event field.");
            }
            bool numeric = dimensions.Find(column)?.Kind is DimensionValueKind.Int64 or DimensionValueKind.UInt64;
            return (column, numeric, 1);
        }
        ComputedDimension Resolve(string name)
        {
            ComputedDimension? cached = null;
            if (resolved.TryGetValue(name, out cached))
            {
                return cached;
            }
            if (!visiting.Add(name) || visiting.Count > 8)
            {
                throw new InvalidOperationException("Computed dimensions contain a cycle or dependency depth above eight.");
            }
            ComputedDimensionOptions declaration = declarations[name];
            string? sourceName = declaration.Source?.Trim();
            string? operation = declaration.Operation?.Trim().ToLowerInvariant();
            if (declaration.Version != 1 || operation is not ("map" or "lower" or "upper" or "trim" or "substring" or "bucket"))
            {
                throw new InvalidOperationException("Computed dimensions require version 1 and a supported operation.");
            }
            (string source, bool numeric, int depth) = ResolveSource(sourceName);
            if (depth > 8 || operation == "bucket" && !numeric)
            {
                throw new InvalidOperationException("Computed dimension source type or expanded depth is incompatible.");
            }
            IReadOnlyList<DimensionMapping> mapping = CompileMapping(declaration, operation);
            if (declaration.Start is < 1 or > 256 || declaration.Length is < 1 or > 256
                || operation != "substring" && (declaration.Start != 1 || declaration.Length != 64))
            {
                throw new InvalidOperationException("Computed substring settings require supported bounds and the substring operation.");
            }
            if (operation == "bucket" ? declaration.Width is not double width || !double.IsFinite(width) || width <= 0
                : declaration.Width is not null)
            {
                throw new InvalidOperationException("Numeric buckets require a finite positive width; other operations cannot declare width.");
            }
            ComputedDimension definition = new(name, 1, sourceName!, operation, mapping, declaration.Default,
                declaration.Start, declaration.Length, declaration.Width)
            {
                Numeric = operation == "bucket",
                Depth = depth
            };
            string expression = ComputedDimensionSql.Build(definition, source);
            if (expression.Length > 4096)
            {
                throw new InvalidOperationException("Computed dimension expanded expression exceeds 4096 characters.");
            }
            definition = definition with { Expression = expression };
            visiting.Remove(name);
            resolved.Add(name, definition);
            return definition;
        }
        Declared = Array.AsReadOnly(declarations.Keys.Select(Resolve).ToArray());
        _byName = resolved.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public IReadOnlyList<ComputedDimension> Declared { get; }

    public ComputedDimension? Find(string? name) =>
        name is not null && _byName.TryGetValue(name, out ComputedDimension? definition) ? definition : null;

    private static IReadOnlyList<DimensionMapping> CompileMapping(ComputedDimensionOptions declaration, string operation)
    {
        if (declaration.Mapping is null || declaration.Mapping.Count > 32 || declaration.Default?.Length > 256
            || operation != "map" && (declaration.Mapping.Count > 0 || declaration.Default is not null)
            || operation == "map" && declaration.Mapping.Count == 0)
        {
            throw new InvalidOperationException("Map requires one to 32 bounded entries; mapping/default settings belong only to map.");
        }
        HashSet<string> keys = new(StringComparer.Ordinal);
        DimensionMapping[] mapping = new DimensionMapping[declaration.Mapping.Count];
        for (int index = 0; index < mapping.Length; index++)
        {
            DimensionMappingOptions entry = declaration.Mapping[index]
                ?? throw new InvalidOperationException("Computed mapping entry is null.");
            if (entry.From is null || entry.To is null || entry.From.Length > 256 || entry.To.Length > 256 || !keys.Add(entry.From))
            {
                throw new InvalidOperationException("Computed map keys must be unique and keys/values must not exceed 256 characters.");
            }
            mapping[index] = new DimensionMapping(entry.From, entry.To);
        }
        return Array.AsReadOnly(mapping);
    }
}

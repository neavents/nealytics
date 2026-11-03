namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

internal static class ProjectSchemaUnion
{
    internal static StorageSchema Compile(TelemetryEngineOptions limits, ProjectContext baseline, IReadOnlyList<ProjectContext> configured)
    {
        ProjectContext[] contexts = configured.Prepend(baseline).ToArray();
        Dictionary<string, Dimension> dimensions = new(StringComparer.Ordinal);
        Dictionary<string, Measure> measures = new(StringComparer.Ordinal);
        Dictionary<string, Rollup> rollups = new(StringComparer.Ordinal);
        foreach (ProjectContext context in contexts)
        {
            foreach (Dimension column in context.Dimensions.Declared)
            {
                Dimension? previous = null;
                dimensions[column.Name] = dimensions.TryGetValue(column.Name, out previous)
                    ? Merge(previous, column) : column;
            }
            foreach (Measure column in context.Measures.Declared)
            {
                Measure? previous = null;
                measures[column.Name] = measures.TryGetValue(column.Name, out previous)
                    ? Merge(previous, column) : column;
            }
            foreach (Rollup rollup in context.Rollups.Declared)
            {
                Rollup? previous = null;
                if (rollups.TryGetValue(rollup.Name, out previous)
                    && (RollupRegistry.BuildTableDdl(previous) != RollupRegistry.BuildTableDdl(rollup)
                        || RollupRegistry.BuildViewDdl(previous) != RollupRegistry.BuildViewDdl(rollup)))
                {
                    throw new InvalidOperationException($"Project schemas declare incompatible rollup '{rollup.Name}'.");
                }
                rollups[rollup.Name] = rollup;
            }
        }
        foreach (string column in measures.Keys)
        {
            if (dimensions.ContainsKey(column))
            {
                throw Incompatible(column);
            }
        }
        TelemetryEngineOptions union = new()
        {
            MaxDimensions = limits.MaxDimensions,
            MaxMeasures = limits.MaxMeasures,
            Dimensions = dimensions.Values.Select(column => new DimensionOptions
            {
                Name = column.Name, Type = column.ConfiguredType, Retired = column.Retired
            }).ToList(),
            Measures = measures.Values.Select(column => new MeasureOptions
            {
                Name = column.Name,
                Type = column.ConfiguredType,
                Aggregations = string.Join(',', column.Aggregations),
                Minimum = column.Minimum,
                Maximum = column.Maximum,
                Retired = column.Retired
            }).ToList(),
            TenantAttributes = contexts.SelectMany(context => context.TenantAttributes.Declared)
                .Distinct(StringComparer.Ordinal).Select(name => new TenantAttributeOptions { Name = name }).ToList()
        };
        DimensionRegistry physicalDimensions = new(union);
        MeasureRegistry physicalMeasures = new(union, physicalDimensions);
        return new StorageSchema(physicalDimensions, physicalMeasures, new TenantAttributeRegistry(union),
            RollupRegistry.FromDefinitions(rollups.Values.ToArray()));
    }

    private static Dimension Merge(Dimension left, Dimension right)
    {
        if (left.ClickHouseType != right.ClickHouseType || left.DefaultExpression != right.DefaultExpression)
        {
            throw Incompatible(left.Name);
        }
        return left with { Retired = left.Retired && right.Retired };
    }

    private static Measure Merge(Measure left, Measure right)
    {
        if (left.ClickHouseType != right.ClickHouseType)
        {
            throw Incompatible(left.Name);
        }
        return left with
        {
            Aggregations = left.Aggregations.Concat(right.Aggregations).ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            Minimum = left.Minimum is null || right.Minimum is null ? null : Math.Min(left.Minimum.Value, right.Minimum.Value),
            Maximum = left.Maximum is null || right.Maximum is null ? null : Math.Max(left.Maximum.Value, right.Maximum.Value),
            Retired = left.Retired && right.Retired
        };
    }

    private static InvalidOperationException Incompatible(string column) =>
        new($"Project schemas declare incompatible types for physical column '{column}'.");
}

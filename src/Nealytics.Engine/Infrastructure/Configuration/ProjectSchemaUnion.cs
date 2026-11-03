namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;

internal static class ProjectSchemaUnion
{
    internal static StorageSchema Compile(TelemetryEngineOptions limits, ProjectContext baseline, IReadOnlyList<ProjectContext> configured)
    {
        ProjectContext[] contexts = configured.Prepend(baseline).ToArray();
        TelemetryEngineOptions union = new()
        {
            MaxDimensions = limits.MaxDimensions,
            MaxMeasures = limits.MaxMeasures
        };
        foreach (IGrouping<string, Dimension> group in contexts.SelectMany(context => context.Dimensions.Declared).GroupBy(column => column.Name, StringComparer.Ordinal))
        {
            Dimension first = group.First();
            if (group.Any(column => column.ClickHouseType != first.ClickHouseType || column.DefaultExpression != first.DefaultExpression))
            {
                throw new InvalidOperationException($"Project schemas declare incompatible types for physical column '{group.Key}'.");
            }
            union.Dimensions.Add(new DimensionOptions { Name = first.Name, Type = first.ConfiguredType, Retired = group.All(column => column.Retired) });
        }
        foreach (IGrouping<string, Measure> group in contexts.SelectMany(context => context.Measures.Declared).GroupBy(column => column.Name, StringComparer.Ordinal))
        {
            Measure first = group.First();
            if (group.Any(column => column.ClickHouseType != first.ClickHouseType)
                || union.Dimensions.Any(dimension => dimension.Name == first.Name))
            {
                throw new InvalidOperationException($"Project schemas declare incompatible types for physical column '{group.Key}'.");
            }
            union.Measures.Add(new MeasureOptions
            {
                Name = first.Name,
                Type = first.ConfiguredType,
                Aggregations = string.Join(',', group.SelectMany(column => column.Aggregations).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)),
                Minimum = group.Any(column => column.Minimum is null) ? null : group.Min(column => column.Minimum),
                Maximum = group.Any(column => column.Maximum is null) ? null : group.Max(column => column.Maximum),
                Retired = group.All(column => column.Retired)
            });
        }
        union.TenantAttributes = contexts.SelectMany(context => context.TenantAttributes.Declared)
            .Distinct(StringComparer.Ordinal).Select(name => new TenantAttributeOptions { Name = name }).ToList();
        Dictionary<string, Rollup> rollups = new(StringComparer.Ordinal);
        foreach (Rollup rollup in contexts.SelectMany(context => context.Rollups.Declared))
        {
            if (rollups.TryGetValue(rollup.Name, out Rollup? previous)
                && (RollupRegistry.BuildTableDdl(previous) != RollupRegistry.BuildTableDdl(rollup)
                    || RollupRegistry.BuildViewDdl(previous) != RollupRegistry.BuildViewDdl(rollup)))
            {
                throw new InvalidOperationException($"Project schemas declare incompatible rollup '{rollup.Name}'.");
            }
            rollups[rollup.Name] = rollup;
        }
        DimensionRegistry dimensions = new(union);
        MeasureRegistry measures = new(union, dimensions);
        TenantAttributeRegistry attributes = new(union);
        RollupRegistry registry = RollupRegistry.FromDefinitions(rollups.Values.ToArray());
        return new StorageSchema(dimensions, measures, attributes, registry);
    }
}

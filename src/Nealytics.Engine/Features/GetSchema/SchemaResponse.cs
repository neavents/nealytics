namespace Nealytics.Engine.Features.GetSchema;

using System;
using System.Collections.Generic;
using System.Linq;

public sealed class SchemaDimension
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool Groupable { get; init; }
    public bool Filterable { get; init; }
    public long NonEmptyCount { get; init; }
    public bool Populated { get; init; }
}

public sealed class SchemaMeasure
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public IReadOnlyList<string> Aggregations { get; init; } = Array.Empty<string>();
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public long NonEmptyCount { get; init; }
    public bool Populated { get; init; }
}

public sealed class SchemaRollup
{
    public string Name { get; init; } = string.Empty;

    public string Grain { get; init; } = string.Empty;

    public IReadOnlyList<string> EventTypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Dimensions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Measures { get; init; } = Array.Empty<string>();

    public static SchemaRollup From(Nealytics.Engine.Infrastructure.Configuration.Rollup rollup)
    {
        ArgumentNullException.ThrowIfNull(rollup);

        return new SchemaRollup
        {
            Name = rollup.Name,
            Grain = rollup.Grain switch
            {
                Nealytics.Engine.Infrastructure.Configuration.RollupGrain.Hour => "hour",
                Nealytics.Engine.Infrastructure.Configuration.RollupGrain.Session => "session",
                _ => "day",
            },
            EventTypes = [.. rollup.EventTypes.Order(StringComparer.Ordinal)],
            Dimensions = [.. rollup.Dimensions.Select(dimension => dimension.Name)],
            Measures = [.. rollup.Measures.Select(measure => $"{measure.Measure.Name}:{measure.Aggregation}")],
        };
    }
}

public sealed class SchemaEventType
{
    public string Name { get; init; } = string.Empty;
    public long Count { get; init; }
}

public sealed class SchemaResponse
{
    public IReadOnlyList<string> CoreColumns { get; init; } = Array.Empty<string>();

    public IReadOnlyList<SchemaDimension> Dimensions { get; init; } = Array.Empty<SchemaDimension>();

    public IReadOnlyList<SchemaMeasure> Measures { get; init; } = Array.Empty<SchemaMeasure>();

    public IReadOnlyList<string> Metrics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Grains { get; init; } = Array.Empty<string>();

    public IReadOnlyList<SchemaRollup> Rollups { get; init; } = Array.Empty<SchemaRollup>();

    public IReadOnlyList<SchemaEventType> EventTypes { get; init; } = Array.Empty<SchemaEventType>();

    public int RetentionDays { get; init; }

    public bool EventTypesAvailable { get; init; }

    public bool PopulationAvailable { get; init; }
}

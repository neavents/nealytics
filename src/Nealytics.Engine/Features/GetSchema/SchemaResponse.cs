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

/// <summary>
/// A declared rollup, so a caller can tell which questions this deployment has pre-aggregated.
///
/// Reported for the same reason the response says which source answered a query: a rollup is the
/// difference between a chart that loads and one that scans the whole table, and a client building
/// itself from this endpoint has no other way to know one exists. `retentionDays` aside, this is
/// also the only place a reader learns that a range must land on a bucket boundary to benefit.
/// </summary>
public sealed class SchemaRollup
{
    public string Name { get; init; } = string.Empty;

    /// <summary>hour | day | session.</summary>
    public string Grain { get; init; } = string.Empty;

    /// <summary>Empty when the rollup covers every event type.</summary>
    public IReadOnlyList<string> EventTypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Dimensions { get; init; } = Array.Empty<string>();

    /// <summary>Stored states, as <c>measure:aggregation</c>.</summary>
    public IReadOnlyList<string> Measures { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Built here rather than inline in the endpoint, so the grain names this reports can be tested.
    /// They are a wire contract — a client decides whether to align a range on them — and an enum
    /// renamed in C# must not silently rename a field in someone's dashboard.
    /// </summary>
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

    /// <summary>
    /// How long raw events are kept. Aggregates in a rollup outlive it, which is exactly why a
    /// rollup is worth declaring for anything you want to look at next year.
    /// </summary>
    public int RetentionDays { get; init; }

    public bool EventTypesAvailable { get; init; }

    /// <summary>
    /// Whether <c>populated</c> and <c>nonEmptyCount</c> above were actually measured.
    ///
    /// A declared dimension that no producer sends reads, everywhere else, exactly like a dimension
    /// whose venue had no traffic. That gap is where this estate's silent failures live: `menu_id`
    /// was declared and queried for months while carrying the packed payload's dense id, and the
    /// leaderboard built on it rendered two healthy-looking bars labelled "0" and "01MENU".
    ///
    /// When this is false the counts are absent rather than zero, and a caller must not read them
    /// as "the producer stopped sending it" — a failed census and an empty column are different
    /// facts, and only one of them is a bug.
    /// </summary>
    public bool PopulationAvailable { get; init; }
}

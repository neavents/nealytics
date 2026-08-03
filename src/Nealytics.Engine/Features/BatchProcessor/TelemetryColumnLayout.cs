namespace Nealytics.Engine.Features.BatchProcessor;

using System.Collections.Generic;
using System.Linq;
using Nealytics.Engine.Infrastructure.Configuration;

/// <summary>
/// The column list for an insert: fixed core columns, then the declared dimensions in registry
/// order.
///
/// This exists so the list is written down exactly once. The INSERT statement's column names and
/// the value-array dictionary handed to the driver are both derived from
/// <see cref="ColumnNames"/> — if they were built by two separate iterations they could disagree,
/// and a column misalignment does not throw and is not noticed: it puts tenant ids in the browser
/// column and reports success.
///
/// Registry order is config order, which makes the insert shape stable across restarts.
/// </summary>
internal sealed class TelemetryColumnLayout
{
    /// <summary>
    /// Columns the engine always writes. No deployment vocabulary here and none permitted —
    /// everything domain-specific arrives through <see cref="Dimensions"/>.
    /// </summary>
    internal static readonly IReadOnlyList<string> CoreColumns =
    [
        "event_id",
        "project_id",
        "tenant_id",
        "session_id",
        "user_id",
        "event_type",
        "object_id",
        "device_class",
        "os",
        "browser",
        "country",
        "metadata_json",
        "timestamp",
    ];

    internal TelemetryColumnLayout(DimensionRegistry registry)
    {
        Dimensions = registry.Active;
        ColumnNames = [.. CoreColumns, .. Dimensions.Select(dimension => dimension.Name)];
    }

    /// <summary>Active dimensions, in registry order — the order they appear after the core columns.</summary>
    internal IReadOnlyList<Dimension> Dimensions { get; }

    /// <summary>Core columns followed by dimension columns. The one list.</summary>
    internal IReadOnlyList<string> ColumnNames { get; }
}

namespace Nealytics.Engine.Features.BatchProcessor;

using System.Collections.Generic;
using System.Linq;
using Nealytics.Engine.Infrastructure.Configuration;

internal sealed class TelemetryColumnLayout
{
    internal static readonly IReadOnlyList<string> CoreColumns =
    [
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
    ];

    internal TelemetryColumnLayout(DimensionRegistry registry, MeasureRegistry measures)
    {
        Dimensions = registry.Active;
        Measures = measures.Active;
        ColumnNames =
        [
            .. CoreColumns,
            .. Dimensions.Select(dimension => dimension.Name),
            .. Measures.Select(measure => measure.Name),
        ];
    }

    internal IReadOnlyList<Dimension> Dimensions { get; }

    internal IReadOnlyList<Measure> Measures { get; }

    internal IReadOnlyList<string> ColumnNames { get; }
}

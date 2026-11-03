namespace Nealytics.Engine.Infrastructure.Configuration;

using Nealytics.Engine.Infrastructure.Query;

public sealed record ProjectContext(
    string? ProjectId,
    DimensionRegistry Dimensions,
    MeasureRegistry Measures,
    TenantAttributeRegistry TenantAttributes,
    ComputedDimensionRegistry ComputedDimensions,
    QueryColumns Columns,
    AnalyticsColumns Analytics,
    NamedMetricRegistry NamedMetrics,
    EventSegmentRegistry EventSegments,
    SavedReportRegistry SavedReports,
    RollupRegistry Rollups)
{
    internal static ProjectContext Compile(string projectId, TelemetryEngineOptions options)
    {
        DimensionRegistry dimensions = new(options);
        MeasureRegistry measures = new(options, dimensions);
        TenantAttributeRegistry attributes = new(options);
        ComputedDimensionRegistry computed = new(options, dimensions, measures);
        QueryColumns columns = new(dimensions, attributes);
        AnalyticsColumns analytics = new(new QueryColumns(dimensions, attributes, computed));
        NamedMetricRegistry metrics = new(options, measures);
        EventSegmentRegistry segments = new(options, analytics.Value, measures);
        SavedReportRegistry reports = new(options, metrics, segments);
        RollupRegistry rollups = new(options, dimensions, measures, attributes);
        return new ProjectContext(projectId, dimensions, measures, attributes, computed, columns, analytics,
            metrics, segments, reports, rollups);
    }
}

public sealed record StorageSchema(
    DimensionRegistry Dimensions, MeasureRegistry Measures, TenantAttributeRegistry TenantAttributes, RollupRegistry Rollups);

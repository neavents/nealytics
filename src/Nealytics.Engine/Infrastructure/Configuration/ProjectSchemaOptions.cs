namespace Nealytics.Engine.Infrastructure.Configuration;

using System.Collections.Generic;

public sealed class ProjectSchemaOptions
{
    public string ProjectId { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public List<DimensionOptions> Dimensions { get; set; } = [];
    public List<MeasureOptions> Measures { get; set; } = [];
    public List<TenantAttributeOptions> TenantAttributes { get; set; } = [];
    public List<ComputedDimensionOptions> ComputedDimensions { get; set; } = [];
    public List<NamedMetricOptions> NamedMetrics { get; set; } = [];
    public List<EventSegmentOptions> EventSegments { get; set; } = [];
    public List<SavedReportOptions> SavedReports { get; set; } = [];
    public List<RollupOptions> Rollups { get; set; } = [];

    internal TelemetryEngineOptions Apply(TelemetryEngineOptions limits) => new()
    {
        Dimensions = Dimensions,
        Measures = Measures,
        TenantAttributes = TenantAttributes,
        ComputedDimensions = ComputedDimensions,
        NamedMetrics = NamedMetrics,
        EventSegments = EventSegments,
        SavedReports = SavedReports,
        Rollups = Rollups,
        MaxDimensions = limits.MaxDimensions,
        MaxMeasures = limits.MaxMeasures,
        MaxComputedDimensions = limits.MaxComputedDimensions,
        MaxNamedMetrics = limits.MaxNamedMetrics,
        MaxEventSegments = limits.MaxEventSegments,
        MaxSavedReports = limits.MaxSavedReports,
        MaxQueryLimit = limits.MaxQueryLimit,
        AliasEventType = limits.AliasEventType,
        ClusterName = limits.ClusterName
    };
}

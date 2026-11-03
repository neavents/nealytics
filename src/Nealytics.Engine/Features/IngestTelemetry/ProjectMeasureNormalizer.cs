namespace Nealytics.Engine.Features.IngestTelemetry;

using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

internal static class ProjectMeasureNormalizer
{
    internal static void Normalize(GlobalTelemetryPayload payload, ProjectCatalog? catalog)
    {
        if (catalog?.Enabled != true || payload.Measures is null)
        {
            return;
        }
        ProjectContext? context = catalog.Resolve(payload.ProjectId);
        if (context is null)
        {
            return;
        }
        foreach (KeyValuePair<string, string> cell in payload.Measures)
        {
            Measure? measure = context.Measures.Find(cell.Key);
            if (measure is not null && (measure.Minimum is not null || measure.Maximum is not null)
                && !string.IsNullOrEmpty(cell.Value) && !MeasureValueRules.Accepts(measure, cell.Value))
            {
                payload.Measures[cell.Key] = string.Empty;
            }
        }
    }
}

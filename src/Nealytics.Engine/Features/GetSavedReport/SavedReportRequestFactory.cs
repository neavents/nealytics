namespace Nealytics.Engine.Features.GetSavedReport;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Features.GetNamedMetric;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;

public static class SavedReportRequestFactory
{
    public static SavedReportRequestResult Create(
        ReadIdentity identity,
        string name,
        string? from,
        string? to,
        string? traffic,
        IReadOnlyList<string> filters,
        SavedReportRegistry reports,
        NamedMetricRegistry metrics,
        EventSegmentRegistry segments,
        QueryColumns columns,
        MeasureRegistry measures,
        int defaultRangeHours,
        DateTime nowUtc)
    {
        if (identity.Rejected)
        {
            return new(null, identity.Outcome == ReadIdentityOutcome.BadRequest ? 400 : 403, identity.ErrorMessage);
        }
        if (string.IsNullOrWhiteSpace(identity.ProjectId)
            || (string.IsNullOrWhiteSpace(identity.TenantId) && identity.TenantSet is null))
        {
            return new(null, 403, null);
        }
        if (identity.ProjectId.Length > 256 || identity.TenantId?.Length > 256)
        {
            return new(null, 400, "Project and tenant identifiers must not exceed 256 characters.");
        }
        SavedReport? definition = reports.Find(name);
        if (definition is null)
        {
            return new(null, 404, "Unknown saved report.");
        }
        NamedMetricRequestResult parsed = NamedMetricRequestFactory.Create(
            identity, definition.Fields[0].Metric, from, to, traffic, filters,
            metrics, columns, measures, defaultRangeHours, nowUtc, segments, definition.Segment);
        if (parsed.Request is null)
        {
            return new(null, parsed.StatusCode, parsed.Error);
        }
        QueryScope selected = parsed.Request.Scope;
        for (int index = 1; index < definition.Fields.Count; index++)
        {
            SavedReportField field = definition.Fields[index];
            NamedMetric metric = field.Definition;
            string? violation = MetricProjection.UnitViolation(metric, measures, selected.Filters);
            if (violation is not null)
            {
                return new(null, 400, $"Field '{field.Name}': {violation}");
            }
        }
        QueryScope scope = new QueryScope
        {
            ProjectId = selected.ProjectId,
            TenantId = selected.TenantId,
            TenantSet = selected.TenantSet,
            From = selected.From,
            To = selected.To,
            TrafficClass = selected.TrafficClass,
            Filters = selected.Filters
        };
        return new(new SavedReportRequest(definition, scope), 200, null);
    }

    internal static string? UnsupportedParameter(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (name is not ("from" or "to" or "traffic" or "filter" or "tenant"))
            {
                return "Only from, to, traffic, filter and tenant are supported report parameters.";
            }
        }
        return null;
    }
}

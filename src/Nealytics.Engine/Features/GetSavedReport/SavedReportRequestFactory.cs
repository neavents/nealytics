namespace Nealytics.Engine.Features.GetSavedReport;

using System;
using System.Collections.Generic;
using System.Linq;
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
        DateTime nowUtc,
        IReadOnlyDictionary<string, string[]>? parameters = null,
        string? execution = null,
        string? accuracy = null)
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
        string? executionViolation = AnalyticsExecution.Violation(execution, accuracy, definition.Fields.Select(field => field.Definition));
        if (executionViolation is not null)
        {
            return new(null, 400, executionViolation);
        }
        ReportParameterResult bound = ReportParameterBinding.Bind(definition.Parameters, parameters);
        if (bound.Error is not null)
        {
            return new(null, 400, bound.Error);
        }
        NamedMetricRequestResult parsed = NamedMetricRequestFactory.Create(
            identity, definition.Fields[0].Metric, from, to, traffic, filters,
            metrics, columns, measures, defaultRangeHours, nowUtc, segments, definition.Segment, bound.Filters, definition.GroupBy,
            execution, accuracy);
        if (parsed.Request is null)
        {
            return new(null, parsed.StatusCode, parsed.Error);
        }
        QueryScope selected = parsed.Request.Scope;
        for (int index = 1; index < definition.Fields.Count; index++)
        {
            SavedReportField field = definition.Fields[index];
            NamedMetric metric = field.Definition;
            string? violation = MetricProjection.UnitViolation(metric, measures, selected.Filters, definition.GroupBy);
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
        return new(new SavedReportRequest(definition, scope) { Execution = parsed.Request.Execution }, 200, null);
    }

    internal static string? UnsupportedParameter(IEnumerable<string> names, IReadOnlyList<ReportParameter>? parameters = null)
    {
        foreach (string name in names)
        {
            if (name is not ("from" or "to" or "traffic" or "filter" or "tenant" or "execution" or "accuracy")
                && !(name.StartsWith("param.", StringComparison.Ordinal)
                    && parameters is not null && parameters.Any(parameter => parameter.Name == name[6..])))
            {
                return "Only from, to, traffic, filter, tenant, execution and accuracy are supported report parameters.";
            }
        }
        return null;
    }
}

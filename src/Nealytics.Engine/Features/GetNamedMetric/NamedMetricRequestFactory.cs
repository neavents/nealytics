namespace Nealytics.Engine.Features.GetNamedMetric;

using System;
using System.Collections.Generic;
using System.Globalization;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;

public static class NamedMetricRequestFactory
{
    public static NamedMetricRequestResult Create(
        ReadIdentity identity,
        string name,
        string? from,
        string? to,
        string? traffic,
        IReadOnlyList<string> filters,
        NamedMetricRegistry metrics,
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
        NamedMetric? definition = metrics.Find(name);
        if (definition is null)
        {
            return new(null, 404, "Unknown named metric.");
        }
        DateTime end = Milliseconds(nowUtc.ToUniversalTime());
        if (end.Year is < 1900 or > 2299 || defaultRangeHours is < 1 or > 3_506_328)
        {
            return new(null, 400, "The configured lookback and current time must fit the supported range.");
        }
        DateTime start = end.AddHours(-defaultRangeHours);
        if (!TryTime(from, start, out start) || !TryTime(to, end, out end) || start >= end)
        {
            return new(null, 400, "Use a valid UTC range [from, to) within years 1900 through 2299.");
        }
        if (!TrafficFilter.TryParse(traffic, out string? trafficClass))
        {
            return new(null, 400, TrafficFilter.Rejection(traffic));
        }
        if (filters.Count > 32)
        {
            return new(null, 400, "At most 32 filters are supported.");
        }
        FilterParser.Result parsed = FilterParser.Parse(filters, columns, measures, 256);
        if (parsed.Outcome != FilterParser.Outcome.Ok)
        {
            return new(null, 400, FilterRejection.Message(parsed, columns, measures, 256));
        }
        if (definition.Measure is string measureName)
        {
            string? violation = UnitSafety.Violation(measures.Find(measureName)!, definition.Aggregation, null, parsed.Filters);
            if (violation is not null)
            {
                return new(null, 400, violation);
            }
        }
        QueryScope scope = new QueryScope
        {
            ProjectId = identity.ProjectId,
            TenantId = identity.TenantId ?? string.Empty,
            TenantSet = identity.TenantSet,
            From = start,
            To = end,
            TrafficClass = trafficClass,
            EventType = definition.EventType,
            Filters = parsed.Filters
        };
        return new(new NamedMetricRequest(definition, scope), 200, null);
    }

    private static bool TryTime(string? value, DateTime fallback, out DateTime result)
    {
        result = fallback;
        if (!string.IsNullOrEmpty(value))
        {
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
            {
                return false;
            }
            result = Milliseconds(parsed.UtcDateTime);
        }
        return result.Year is >= 1900 and <= 2299;
    }

    private static DateTime Milliseconds(DateTime value) =>
        new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
}

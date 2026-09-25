namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;
using System.Collections.Generic;
using System.Globalization;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public readonly struct EventTimeSeriesRequestResult
{
    public bool Success { get; init; }
    public int ErrorStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public EventTimeSeriesRequest Request { get; init; }

    public static EventTimeSeriesRequestResult Fail(int statusCode, string? message) => new EventTimeSeriesRequestResult
    {
        Success = false,
        ErrorStatusCode = statusCode,
        ErrorMessage = message
    };

    public static EventTimeSeriesRequestResult Ok(EventTimeSeriesRequest request) => new EventTimeSeriesRequestResult
    {
        Success = true,
        Request = request
    };
}

public static class EventTimeSeriesRequestFactory
{
    public const int MaxFieldLength = 256;
    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;

    public static EventTimeSeriesRequestResult Create(
        string? projectId,
        string? tenantId,
        string? limitRaw,
        string? intervalRaw,
        string? fromRaw,
        string? toRaw,
        string? eventType,
        string? groupByRaw,
        string? tzRaw,
        string? trafficRaw,
        IReadOnlyList<string> filtersRaw,
        QueryColumns columns,
        MeasureRegistry measures,
        int maxLimit,
        int defaultRangeHours,
        DateTime nowUtc) =>
        Create(
            projectId, tenantId, limitRaw, intervalRaw, fromRaw, toRaw, eventType, groupByRaw, tzRaw, trafficRaw,
            filtersRaw, null, columns, measures, maxLimit, defaultRangeHours, nowUtc);

    public static EventTimeSeriesRequestResult Create(
        string? projectId,
        string? tenantId,
        string? limitRaw,
        string? intervalRaw,
        string? fromRaw,
        string? toRaw,
        string? eventType,
        string? groupByRaw,
        string? tzRaw,
        string? trafficRaw,
        IReadOnlyList<string> filtersRaw,
        string? metricRaw,
        QueryColumns columns,
        MeasureRegistry measures,
        int maxLimit,
        int defaultRangeHours,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(tenantId))
        {
            return EventTimeSeriesRequestResult.Fail(StatusForbidden, null);
        }

        if (projectId.Length > MaxFieldLength || tenantId.Length > MaxFieldLength)
        {
            return EventTimeSeriesRequestResult.Fail(StatusBadRequest, "Project ID and Tenant ID must not exceed 256 characters.");
        }

        TimeSeriesInterval interval = TimeSeriesInterval.Hour;
        if (!string.IsNullOrEmpty(intervalRaw) && !TimeSeriesIntervalParser.TryParse(intervalRaw, out interval))
        {
            return EventTimeSeriesRequestResult.Fail(StatusBadRequest, "'interval' must be one of: minute, hour, day.");
        }

        int limit = maxLimit;
        if (!string.IsNullOrEmpty(limitRaw) && int.TryParse(limitRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit))
        {
            limit = Math.Clamp(parsedLimit, 1, maxLimit);
        }

        DateTime fromUtc = nowUtc.AddHours(-defaultRangeHours);
        DateTime toUtc = nowUtc;

        if (!string.IsNullOrEmpty(fromRaw)
            && DateTime.TryParse(fromRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsedFrom))
        {
            fromUtc = parsedFrom;
        }

        if (!string.IsNullOrEmpty(toRaw)
            && DateTime.TryParse(toRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsedTo))
        {
            toUtc = parsedTo;
        }

        if (fromUtc > toUtc)
        {
            return EventTimeSeriesRequestResult.Fail(StatusBadRequest, "'from' must be before or equal to 'to'.");
        }

        string? normalizedEventType = string.IsNullOrWhiteSpace(eventType) ? null : eventType;
        if (normalizedEventType?.Length > MaxFieldLength)
        {
            return EventTimeSeriesRequestResult.Fail(StatusBadRequest, "Filter values must not exceed 256 characters.");
        }

        string? groupByColumn = null;
        if (!string.IsNullOrEmpty(groupByRaw))
        {
            if (measures.IsActive(groupByRaw))
            {
                return EventTimeSeriesRequestResult.Fail(
                    StatusBadRequest,
                    $"'{groupByRaw}' is a declared measure, not a dimension. A measure is a quantity "
                    + "to aggregate, not a series to split by.");
            }

            if (!columns.TryResolve(groupByRaw, out string resolved))
            {
                return EventTimeSeriesRequestResult.Fail(
                    StatusBadRequest, columns.RejectionMessage("groupBy", groupByRaw));
            }

            groupByColumn = resolved;
        }

        string? timeZone = null;
        if (!string.IsNullOrEmpty(tzRaw))
        {
            if (!TimeBucket.IsWellFormed(tzRaw))
            {
                return EventTimeSeriesRequestResult.Fail(
                    StatusBadRequest,
                    "'tz' must be an IANA time zone name such as Europe/Istanbul.");
            }

            timeZone = tzRaw;
        }

        if (!TrafficFilter.TryParse(trafficRaw, out string? trafficClass))
        {
            return EventTimeSeriesRequestResult.Fail(StatusBadRequest, TrafficFilter.Rejection(trafficRaw));
        }

        // The same parser /breakdown uses, so a filter means one thing across the engine.
        FilterParser.Result parsedFilters = FilterParser.Parse(filtersRaw ?? [], columns, measures, MaxFieldLength);

        if (parsedFilters.Outcome != FilterParser.Outcome.Ok)
        {
            return EventTimeSeriesRequestResult.Fail(
                StatusBadRequest, FilterRejection.Message(parsedFilters, columns, measures, MaxFieldLength));
        }

        PivotMetric? metric = null;

        if (!string.IsNullOrEmpty(metricRaw))
        {
            if (metricRaw.Length > MaxFieldLength)
            {
                return EventTimeSeriesRequestResult.Fail(StatusBadRequest, "'metric' must not exceed 256 characters.");
            }

            if (!PivotRequestFactory.TryParseMetric(metricRaw, columns, measures, out PivotMetric parsedMetric, out string? metricError))
            {
                return EventTimeSeriesRequestResult.Fail(StatusBadRequest, metricError);
            }

            metric = parsedMetric;
        }

        return EventTimeSeriesRequestResult.Ok(new EventTimeSeriesRequest
        {
            Metric = metric,
            Filters = parsedFilters.Filters,
            ProjectId = projectId,
            TenantId = tenantId,
            From = fromUtc,
            To = toUtc,
            TrafficClass = trafficClass,
            Interval = interval,
            EventType = normalizedEventType,
            GroupByColumn = groupByColumn,
            TimeZone = timeZone,
            Limit = limit
        });
    }
}

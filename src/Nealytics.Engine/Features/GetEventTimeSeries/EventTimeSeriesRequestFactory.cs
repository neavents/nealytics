namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;
using System.Globalization;

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
            && DateTime.TryParse(fromRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime parsedFrom))
        {
            fromUtc = parsedFrom;
        }

        if (!string.IsNullOrEmpty(toRaw)
            && DateTime.TryParse(toRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime parsedTo))
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

        return EventTimeSeriesRequestResult.Ok(new EventTimeSeriesRequest
        {
            ProjectId = projectId,
            TenantId = tenantId,
            From = fromUtc,
            To = toUtc,
            Interval = interval,
            EventType = normalizedEventType,
            Limit = limit
        });
    }
}

namespace Nealytics.Engine.Features.GetProjectTimeline;

using System;
using System.Globalization;

public readonly struct TimelineRequestResult
{
    public bool Success { get; init; }
    public int ErrorStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public TimelineQueryRequest Request { get; init; }

    public static TimelineRequestResult Fail(int statusCode, string? message) => new TimelineRequestResult
    {
        Success = false,
        ErrorStatusCode = statusCode,
        ErrorMessage = message
    };

    public static TimelineRequestResult Ok(TimelineQueryRequest request) => new TimelineRequestResult
    {
        Success = true,
        Request = request
    };
}

public static class TimelineRequestFactory
{
    public const int DefaultLimit = 100;
    public const int MaxFieldLength = 256;

    public static TimelineRequestResult Create(
        string? projectId,
        string? tenantId,
        string? limitRaw,
        string? beforeRaw,
        string? eventType,
        string? sessionId,
        string? objectId,
        string? metaKey,
        string? metaValue,
        int maxLimit,
        TimeSpan maxRange,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(tenantId))
        {
            return TimelineRequestResult.Fail(StatusForbidden, null);
        }

        if (projectId.Length > MaxFieldLength || tenantId.Length > MaxFieldLength)
        {
            return TimelineRequestResult.Fail(StatusBadRequest, "Project ID and Tenant ID must not exceed 256 characters.");
        }

        int limit = DefaultLimit;
        if (!string.IsNullOrEmpty(limitRaw) && int.TryParse(limitRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit))
        {
            limit = Math.Clamp(parsedLimit, 1, maxLimit);
        }

        DateTime? cursor = null;
        if (!string.IsNullOrEmpty(beforeRaw)
            && DateTime.TryParse(beforeRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsedCursor))
        {
            cursor = parsedCursor.Kind == DateTimeKind.Local ? parsedCursor.ToUniversalTime() : parsedCursor;
        }

        string? normalizedEventType = Normalize(eventType);
        string? normalizedSessionId = Normalize(sessionId);
        string? normalizedObjectId = Normalize(objectId);
        string? normalizedMetaKey = Normalize(metaKey);
        string? normalizedMetaValue = Normalize(metaValue);

        if (normalizedEventType?.Length > MaxFieldLength
            || normalizedSessionId?.Length > MaxFieldLength
            || normalizedObjectId?.Length > MaxFieldLength
            || normalizedMetaKey?.Length > MaxFieldLength
            || normalizedMetaValue?.Length > MaxFieldLength)
        {
            return TimelineRequestResult.Fail(StatusBadRequest, "Filter values must not exceed 256 characters.");
        }

        bool hasMetaFilter = normalizedMetaKey is not null && normalizedMetaValue is not null;

        return TimelineRequestResult.Ok(new TimelineQueryRequest
        {
            ProjectId = projectId,
            TenantId = tenantId,
            Limit = limit,
            Before = cursor,
            NotBefore = (cursor ?? nowUtc) - maxRange,
            EventType = normalizedEventType,
            SessionId = normalizedSessionId,
            ObjectId = normalizedObjectId,
            MetaKey = hasMetaFilter ? normalizedMetaKey : null,
            MetaValue = hasMetaFilter ? normalizedMetaValue : null
        });
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;
}

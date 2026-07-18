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
        string? itemId,
        int maxLimit)
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
            cursor = parsedCursor;
        }

        string? normalizedEventType = Normalize(eventType);
        string? normalizedSessionId = Normalize(sessionId);
        string? normalizedItemId = Normalize(itemId);

        if (normalizedEventType?.Length > MaxFieldLength
            || normalizedSessionId?.Length > MaxFieldLength
            || normalizedItemId?.Length > MaxFieldLength)
        {
            return TimelineRequestResult.Fail(StatusBadRequest, "Filter values must not exceed 256 characters.");
        }

        return TimelineRequestResult.Ok(new TimelineQueryRequest
        {
            ProjectId = projectId,
            TenantId = tenantId,
            Limit = limit,
            Before = cursor,
            EventType = normalizedEventType,
            SessionId = normalizedSessionId,
            ItemId = normalizedItemId
        });
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;
}

namespace Nealytics.Engine.Features.GetTopEvents;

using System;
using System.Globalization;

public readonly struct TopEventsRequestResult
{
    public bool Success { get; init; }
    public int ErrorStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public TopEventsRequest Request { get; init; }

    public static TopEventsRequestResult Fail(int statusCode, string? message) => new TopEventsRequestResult
    {
        Success = false,
        ErrorStatusCode = statusCode,
        ErrorMessage = message
    };

    public static TopEventsRequestResult Ok(TopEventsRequest request) => new TopEventsRequestResult
    {
        Success = true,
        Request = request
    };
}

public static class TopEventsRequestFactory
{
    public const int MaxFieldLength = 256;
    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;
    public const int DefaultLimit = 20;

    public static TopEventsRequestResult Create(
        string? projectId,
        string? tenantId,
        string? limitRaw,
        string? dimensionRaw,
        string? fromRaw,
        string? toRaw,
        int maxLimit,
        int defaultRangeHours,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(tenantId))
        {
            return TopEventsRequestResult.Fail(StatusForbidden, null);
        }

        if (projectId.Length > MaxFieldLength || tenantId.Length > MaxFieldLength)
        {
            return TopEventsRequestResult.Fail(StatusBadRequest, "Project ID and Tenant ID must not exceed 256 characters.");
        }

        TopDimension dimension = TopDimension.EventType;
        if (!string.IsNullOrEmpty(dimensionRaw) && !TopDimensionParser.TryParse(dimensionRaw, out dimension))
        {
            return TopEventsRequestResult.Fail(StatusBadRequest, "'dimension' must be one of: event_type, item_id.");
        }

        int limit = Math.Clamp(DefaultLimit, 1, maxLimit);
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
            return TopEventsRequestResult.Fail(StatusBadRequest, "'from' must be before or equal to 'to'.");
        }

        return TopEventsRequestResult.Ok(new TopEventsRequest
        {
            ProjectId = projectId,
            TenantId = tenantId,
            From = fromUtc,
            To = toUtc,
            Dimension = dimension,
            Limit = limit
        });
    }
}

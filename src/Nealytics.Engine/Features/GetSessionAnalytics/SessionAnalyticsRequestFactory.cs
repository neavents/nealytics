namespace Nealytics.Engine.Features.GetSessionAnalytics;

using System;
using System.Globalization;

public readonly struct SessionAnalyticsRequestResult
{
    public bool Success { get; init; }
    public int ErrorStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public SessionAnalyticsRequest Request { get; init; }

    public static SessionAnalyticsRequestResult Fail(int statusCode, string? message) => new SessionAnalyticsRequestResult
    {
        Success = false,
        ErrorStatusCode = statusCode,
        ErrorMessage = message
    };

    public static SessionAnalyticsRequestResult Ok(SessionAnalyticsRequest request) => new SessionAnalyticsRequestResult
    {
        Success = true,
        Request = request
    };
}

public static class SessionAnalyticsRequestFactory
{
    public const int DefaultLimit = 100;
    public const int MaxFieldLength = 256;
    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;

    public static SessionAnalyticsRequestResult Create(
        string? projectId,
        string? tenantId,
        string? limitRaw,
        string? fromRaw,
        string? toRaw,
        int maxLimit,
        int defaultRangeHours,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(tenantId))
        {
            return SessionAnalyticsRequestResult.Fail(StatusForbidden, null);
        }

        if (projectId.Length > MaxFieldLength || tenantId.Length > MaxFieldLength)
        {
            return SessionAnalyticsRequestResult.Fail(StatusBadRequest, "Project ID and Tenant ID must not exceed 256 characters.");
        }

        int limit = DefaultLimit;
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
            return SessionAnalyticsRequestResult.Fail(StatusBadRequest, "'from' must be before or equal to 'to'.");
        }

        return SessionAnalyticsRequestResult.Ok(new SessionAnalyticsRequest
        {
            ProjectId = projectId,
            TenantId = tenantId,
            From = fromUtc,
            To = toUtc,
            Limit = limit
        });
    }
}

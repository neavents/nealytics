namespace Nealytics.Engine.Features.GetActiveUsers;

using System;
using System.Globalization;

public readonly struct ActiveUsersRequestResult
{
    public bool Success { get; init; }
    public int ErrorStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public ActiveUsersRequest Request { get; init; }

    public static ActiveUsersRequestResult Fail(int statusCode, string? message) => new ActiveUsersRequestResult
    {
        Success = false,
        ErrorStatusCode = statusCode,
        ErrorMessage = message
    };

    public static ActiveUsersRequestResult Ok(ActiveUsersRequest request) => new ActiveUsersRequestResult
    {
        Success = true,
        Request = request
    };
}

public static class ActiveUsersRequestFactory
{
    public const int MaxFieldLength = 256;
    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;

    public static ActiveUsersRequestResult Create(
        string? projectId,
        string? tenantId,
        string? limitRaw,
        string? intervalRaw,
        string? byRaw,
        string? modeRaw,
        string? fromRaw,
        string? toRaw,
        int maxLimit,
        int defaultRangeHours,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(tenantId))
        {
            return ActiveUsersRequestResult.Fail(StatusForbidden, null);
        }

        if (projectId.Length > MaxFieldLength || tenantId.Length > MaxFieldLength)
        {
            return ActiveUsersRequestResult.Fail(StatusBadRequest, "Project ID and Tenant ID must not exceed 256 characters.");
        }

        ActiveUsersInterval interval = ActiveUsersInterval.Day;
        if (!string.IsNullOrEmpty(intervalRaw) && !ActiveUsersIntervalParser.TryParse(intervalRaw, out interval))
        {
            return ActiveUsersRequestResult.Fail(StatusBadRequest, "'interval' must be one of: day, month.");
        }

        ActiveDimension dimension = ActiveDimension.User;
        if (!string.IsNullOrEmpty(byRaw) && !ActiveDimensionParser.TryParse(byRaw, out dimension))
        {
            return ActiveUsersRequestResult.Fail(StatusBadRequest, "'by' must be one of: user, session.");
        }

        ActiveCountMode mode = ActiveCountMode.Exact;
        if (!string.IsNullOrEmpty(modeRaw) && !ActiveCountModeParser.TryParse(modeRaw, out mode))
        {
            return ActiveUsersRequestResult.Fail(StatusBadRequest, "'mode' must be one of: exact, approx.");
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
            return ActiveUsersRequestResult.Fail(StatusBadRequest, "'from' must be before or equal to 'to'.");
        }

        return ActiveUsersRequestResult.Ok(new ActiveUsersRequest
        {
            ProjectId = projectId,
            TenantId = tenantId,
            From = fromUtc,
            To = toUtc,
            Interval = interval,
            Dimension = dimension,
            Mode = mode,
            Limit = limit
        });
    }
}

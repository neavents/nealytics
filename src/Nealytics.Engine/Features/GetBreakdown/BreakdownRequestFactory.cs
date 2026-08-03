namespace Nealytics.Engine.Features.GetBreakdown;

using System;
using System.Collections.Generic;
using System.Globalization;

public readonly struct BreakdownRequestResult
{
    public bool Success { get; init; }
    public int ErrorStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public BreakdownRequest Request { get; init; }

    public static BreakdownRequestResult Fail(int statusCode, string? message) => new()
    {
        Success = false,
        ErrorStatusCode = statusCode,
        ErrorMessage = message,
    };

    public static BreakdownRequestResult Ok(BreakdownRequest request) => new()
    {
        Success = true,
        Request = request,
    };
}

public static class BreakdownRequestFactory
{
    public const int MaxFieldLength = 256;
    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;
    public const int DefaultLimit = 50;

    /// <summary>
    /// Cap on filters accepted in one request. Each adds a clause and a parameter; an unbounded
    /// list is a cheap way to make the server build an expensive statement.
    /// </summary>
    public const int MaxFilters = 16;

    public static BreakdownRequestResult Create(
        string? projectId,
        string? tenantId,
        string? metricRaw,
        string? groupByRaw,
        string? eventTypeRaw,
        IReadOnlyList<string> filtersRaw,
        string? fromRaw,
        string? toRaw,
        string? limitRaw,
        string? orderByRaw,
        BreakdownColumns columns,
        int maxLimit,
        int defaultRangeHours,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(tenantId))
        {
            return BreakdownRequestResult.Fail(StatusForbidden, null);
        }

        if (projectId.Length > MaxFieldLength || tenantId.Length > MaxFieldLength)
        {
            return BreakdownRequestResult.Fail(
                StatusBadRequest, "Project ID and Tenant ID must not exceed 256 characters.");
        }

        BreakdownMetric metric = BreakdownMetric.Events;
        if (!string.IsNullOrEmpty(metricRaw) && !TryParseMetric(metricRaw, out metric))
        {
            return BreakdownRequestResult.Fail(
                StatusBadRequest, $"'metric' must be one of: events, sessions, users. Got '{metricRaw}'.");
        }

        // The injection boundary. What comes back is the allowlist's own instance, never the
        // caller's string — see BreakdownColumns.TryResolve.
        if (string.IsNullOrWhiteSpace(groupByRaw))
        {
            return BreakdownRequestResult.Fail(
                StatusBadRequest, columns.RejectionMessage("groupBy", groupByRaw));
        }

        if (!columns.TryResolve(groupByRaw, out string groupByColumn))
        {
            return BreakdownRequestResult.Fail(
                StatusBadRequest, columns.RejectionMessage("groupBy", groupByRaw));
        }

        if (filtersRaw.Count > MaxFilters)
        {
            return BreakdownRequestResult.Fail(
                StatusBadRequest, $"At most {MaxFilters} 'filter' parameters are accepted.");
        }

        List<BreakdownFilter> filters = new(filtersRaw.Count);

        foreach (string raw in filtersRaw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // name:value — split on the FIRST colon only, because a value may legitimately
            // contain one (a URL path, a timestamp).
            int separator = raw.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return BreakdownRequestResult.Fail(
                    StatusBadRequest, $"'filter' must be written name:value. Got '{raw}'.");
            }

            string name = raw[..separator];
            string value = raw[(separator + 1)..];

            if (!columns.TryResolve(name, out string filterColumn))
            {
                return BreakdownRequestResult.Fail(
                    StatusBadRequest, columns.RejectionMessage("filter", name));
            }

            if (value.Length > MaxFieldLength)
            {
                return BreakdownRequestResult.Fail(
                    StatusBadRequest, $"'filter' value for '{name}' must not exceed {MaxFieldLength} characters.");
            }

            filters.Add(new BreakdownFilter { Column = filterColumn, Value = value });
        }

        string? eventType = string.IsNullOrWhiteSpace(eventTypeRaw) ? null : eventTypeRaw;
        if (eventType is not null && eventType.Length > MaxFieldLength)
        {
            return BreakdownRequestResult.Fail(
                StatusBadRequest, $"'eventType' must not exceed {MaxFieldLength} characters.");
        }

        BreakdownOrder order = BreakdownOrder.ValueDescending;
        if (!string.IsNullOrEmpty(orderByRaw) && !TryParseOrder(orderByRaw, out order))
        {
            return BreakdownRequestResult.Fail(
                StatusBadRequest,
                $"'orderBy' must be one of: value_desc, value_asc, key_asc. Got '{orderByRaw}'.");
        }

        // Clamped rather than rejected: groupBy=session_id over a busy range would otherwise try to
        // return millions of rows. The response says it was capped; see BreakdownResponse.Truncated.
        int limit = Math.Clamp(DefaultLimit, 1, maxLimit);
        if (!string.IsNullOrEmpty(limitRaw)
            && int.TryParse(limitRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit))
        {
            limit = Math.Clamp(parsedLimit, 1, maxLimit);
        }

        DateTime fromUtc = nowUtc.AddHours(-defaultRangeHours);
        DateTime toUtc = nowUtc;

        if (!string.IsNullOrEmpty(fromRaw)
            && DateTime.TryParse(
                fromRaw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsedFrom))
        {
            fromUtc = parsedFrom;
        }

        if (!string.IsNullOrEmpty(toRaw)
            && DateTime.TryParse(
                toRaw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsedTo))
        {
            toUtc = parsedTo;
        }

        if (fromUtc > toUtc)
        {
            return BreakdownRequestResult.Fail(StatusBadRequest, "'from' must be before or equal to 'to'.");
        }

        return BreakdownRequestResult.Ok(new BreakdownRequest
        {
            ProjectId = projectId,
            TenantId = tenantId,
            Metric = metric,
            GroupByColumn = groupByColumn,
            EventType = eventType,
            Filters = filters,
            From = fromUtc,
            To = toUtc,
            Limit = limit,
            Order = order,
        });
    }

    public static bool TryParseMetric(string? value, out BreakdownMetric metric)
    {
        switch (value)
        {
            case "events":
                metric = BreakdownMetric.Events;
                return true;
            case "sessions":
                metric = BreakdownMetric.Sessions;
                return true;
            case "users":
                metric = BreakdownMetric.Users;
                return true;
            default:
                metric = BreakdownMetric.Events;
                return false;
        }
    }

    public static string ToWireFormat(BreakdownMetric metric) => metric switch
    {
        BreakdownMetric.Events => "events",
        BreakdownMetric.Sessions => "sessions",
        BreakdownMetric.Users => "users",
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    public static bool TryParseOrder(string? value, out BreakdownOrder order)
    {
        switch (value)
        {
            case "value_desc":
                order = BreakdownOrder.ValueDescending;
                return true;
            case "value_asc":
                order = BreakdownOrder.ValueAscending;
                return true;
            case "key_asc":
                order = BreakdownOrder.KeyAscending;
                return true;
            default:
                order = BreakdownOrder.ValueDescending;
                return false;
        }
    }
}

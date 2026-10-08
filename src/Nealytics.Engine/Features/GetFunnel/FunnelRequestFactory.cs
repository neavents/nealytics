namespace Nealytics.Engine.Features.GetFunnel;

using System;
using System.Collections.Generic;
using System.Globalization;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public readonly struct FunnelRequestResult
{
    public bool Success { get; init; }
    public int ErrorStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public FunnelRequest Request { get; init; }

    public static FunnelRequestResult Fail(int statusCode, string? message) => new()
    {
        Success = false,
        ErrorStatusCode = statusCode,
        ErrorMessage = message,
    };

    public static FunnelRequestResult Ok(FunnelRequest request) => new()
    {
        Success = true,
        Request = request,
    };
}

public static class FunnelRequestFactory
{
    public const int MaxFieldLength = 256;
    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;
    public const int MinSteps = 2;
    public const int MaxSteps = 10;
    public const int DefaultWindowSeconds = 1800;
    public const int MaxWindowSeconds = 86_400;
    public const int DefaultSegmentLimit = 20;

    public static FunnelRequestResult Create(
        string? projectId,
        string? tenantId,
        IReadOnlyList<string> stepsRaw,
        string? grainRaw,
        string? breakdownRaw,
        string? windowRaw,
        string? fromRaw,
        string? toRaw,
        string? limitRaw,
        QueryColumns columns,
        MeasureRegistry measures,
        int maxLimit,
        int defaultRangeHours,
        DateTime nowUtc,
        TenantSet? tenantSet = null,
        string? aliasEventType = null)
    {
        if (string.IsNullOrWhiteSpace(projectId) || (string.IsNullOrWhiteSpace(tenantId) && tenantSet is null))
        {
            return FunnelRequestResult.Fail(StatusForbidden, null);
        }

        if (stepsRaw.Count < MinSteps)
        {
            return FunnelRequestResult.Fail(
                StatusBadRequest,
                $"A funnel needs at least {MinSteps} 'step' parameters. One step is a count, not a funnel.");
        }

        if (stepsRaw.Count > MaxSteps)
        {
            return FunnelRequestResult.Fail(
                StatusBadRequest, $"At most {MaxSteps} 'step' parameters are accepted.");
        }

        List<FunnelStep> steps = new(stepsRaw.Count);

        foreach (string raw in stepsRaw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxFieldLength)
            {
                return FunnelRequestResult.Fail(
                    StatusBadRequest, "Each 'step' must be a non-empty value of at most 256 characters.");
            }

            int colon = raw.IndexOf(':', StringComparison.Ordinal);

            if (colon < 0)
            {
                steps.Add(new FunnelStep { EventType = raw });
                continue;
            }

            string eventType = raw[..colon];
            string filter = raw[(colon + 1)..];
            int equals = filter.IndexOf('=', StringComparison.Ordinal);

            if (eventType.Length == 0 || equals <= 0)
            {
                return FunnelRequestResult.Fail(
                    StatusBadRequest,
                    $"'step' must be an event type, optionally with one filter as "
                    + $"eventType:column=value. Got '{raw}'.");
            }

            string columnRaw = filter[..equals];

            if (measures.IsActive(columnRaw))
            {
                return FunnelRequestResult.Fail(
                    StatusBadRequest,
                    $"'{columnRaw}' is a declared measure, not a dimension. A funnel step filters on "
                    + "a dimension.");
            }

            if (!columns.TryResolve(columnRaw, out string resolved))
            {
                return FunnelRequestResult.Fail(
                    StatusBadRequest, columns.RejectionMessage("step filter", columnRaw));
            }

            steps.Add(new FunnelStep
            {
                EventType = eventType,
                FilterColumn = resolved,
                FilterValue = filter[(equals + 1)..],
            });
        }

        FunnelGrain grain = FunnelGrain.Sessions;

        if (!string.IsNullOrEmpty(grainRaw))
        {
            switch (grainRaw)
            {
                case "sessions":
                    grain = FunnelGrain.Sessions;
                    break;
                case "users":
                    grain = FunnelGrain.Users;
                    break;
                case "identities":
                    if (string.IsNullOrEmpty(aliasEventType))
                    {
                        return FunnelRequestResult.Fail(
                            StatusBadRequest,
                            "'grain=identities' needs identity stitching, which is off: "
                            + "TelemetryEngine:AliasEventType is not set.");
                    }

                    grain = FunnelGrain.Identities;
                    break;
                default:
                    return FunnelRequestResult.Fail(
                        StatusBadRequest,
                        string.IsNullOrEmpty(aliasEventType)
                            ? "'grain' must be one of: sessions, users."
                            : "'grain' must be one of: sessions, users, identities.");
            }
        }

        string? breakdownColumn = null;

        if (!string.IsNullOrEmpty(breakdownRaw))
        {
            if (measures.IsActive(breakdownRaw))
            {
                return FunnelRequestResult.Fail(
                    StatusBadRequest,
                    $"'{breakdownRaw}' is a declared measure, not a dimension. A funnel segments by "
                    + "a dimension.");
            }

            if (!columns.TryResolve(breakdownRaw, out string resolvedBreakdown))
            {
                return FunnelRequestResult.Fail(
                    StatusBadRequest, columns.RejectionMessage("breakdownBy", breakdownRaw));
            }

            breakdownColumn = resolvedBreakdown;
        }

        int windowSeconds = DefaultWindowSeconds;

        if (!string.IsNullOrEmpty(windowRaw))
        {
            if (!int.TryParse(windowRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out windowSeconds)
                || windowSeconds < 1
                || windowSeconds > MaxWindowSeconds)
            {
                return FunnelRequestResult.Fail(
                    StatusBadRequest,
                    $"'windowSeconds' must be between 1 and {MaxWindowSeconds}.");
            }
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
            return FunnelRequestResult.Fail(
                StatusBadRequest, "'from' must be before or equal to 'to'.");
        }

        int limit = Math.Clamp(DefaultSegmentLimit, 1, maxLimit);

        if (!string.IsNullOrEmpty(limitRaw)
            && int.TryParse(limitRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit))
        {
            limit = Math.Clamp(parsedLimit, 1, maxLimit);
        }

        return FunnelRequestResult.Ok(new FunnelRequest
        {
            ProjectId = projectId,
            TenantId = tenantId ?? string.Empty,
            TenantSet = tenantSet,
            Steps = steps,
            Grain = grain,
            AliasEventType = grain == FunnelGrain.Identities ? aliasEventType : null,
            BreakdownColumn = breakdownColumn,
            WindowSeconds = windowSeconds,
            From = fromUtc,
            To = toUtc,
            Limit = limit,
        });
    }
}

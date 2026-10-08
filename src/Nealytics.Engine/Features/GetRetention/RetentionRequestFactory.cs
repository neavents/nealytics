namespace Nealytics.Engine.Features.GetRetention;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public readonly struct RetentionRequestResult
{
    public bool Success { get; init; }
    public int ErrorStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public RetentionRequest Request { get; init; }

    public static RetentionRequestResult Fail(int statusCode, string? message) =>
        new() { Success = false, ErrorStatusCode = statusCode, ErrorMessage = message };

    public static RetentionRequestResult Ok(RetentionRequest request) => new() { Success = true, Request = request };
}

public static class RetentionRequestFactory
{
    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;
    public const int MaxFilters = 16;
    public const int MaxPeriods = 120;
    public const int DefaultPeriods = 8;
    public const int MaxFieldLength = RequestParsing.MaxFieldLength;

    public static RetentionRequestResult Create(
        string? projectId,
        string? tenantId,
        string? periodRaw,
        string? byRaw,
        string? eventTypeRaw,
        IReadOnlyList<string> filtersRaw,
        string? fromRaw,
        string? toRaw,
        string? trafficRaw,
        QueryColumns columns,
        MeasureRegistry measures,
        string? aliasEventType,
        DateTime nowUtc,
        TenantSet? tenantSet = null)
    {
        ArgumentNullException.ThrowIfNull(filtersRaw);

        if (string.IsNullOrWhiteSpace(projectId) || (string.IsNullOrWhiteSpace(tenantId) && tenantSet is null))
        {
            return RetentionRequestResult.Fail(StatusForbidden, null);
        }

        if (projectId.Length > MaxFieldLength || tenantId?.Length > MaxFieldLength)
        {
            return RetentionRequestResult.Fail(StatusBadRequest, "Project ID and Tenant ID must not exceed 256 characters.");
        }

        RetentionPeriod period;

        switch (string.IsNullOrEmpty(periodRaw) ? "week" : periodRaw)
        {
            case "day":
                period = RetentionPeriod.Day;
                break;
            case "week":
                period = RetentionPeriod.Week;
                break;
            case "month":
                period = RetentionPeriod.Month;
                break;
            default:
                return RetentionRequestResult.Fail(StatusBadRequest, $"'period' must be one of: day, week, month. Got '{periodRaw}'.");
        }

        bool stitchingOn = !string.IsNullOrEmpty(aliasEventType);
        RetentionActor actor;

        switch (string.IsNullOrEmpty(byRaw) ? "users" : byRaw)
        {
            case "sessions":
                actor = RetentionActor.Sessions;
                break;
            case "users":
                actor = RetentionActor.Users;
                break;
            case "identities" when stitchingOn:
                actor = RetentionActor.Identities;
                break;
            case "identities":
                return RetentionRequestResult.Fail(
                    StatusBadRequest,
                    "'by=identities' needs identity stitching, which is off: TelemetryEngine:AliasEventType is not set.");
            default:
                return RetentionRequestResult.Fail(
                    StatusBadRequest,
                    stitchingOn
                        ? $"'by' must be one of: sessions, users, identities. Got '{byRaw}'."
                        : $"'by' must be one of: sessions, users. Got '{byRaw}'.");
        }

        string? eventType = string.IsNullOrWhiteSpace(eventTypeRaw) ? null : eventTypeRaw;

        if (eventType is not null && eventType.Length > MaxFieldLength)
        {
            return RetentionRequestResult.Fail(StatusBadRequest, "'eventType' must not exceed 256 characters.");
        }

        if (filtersRaw.Count > MaxFilters)
        {
            return RetentionRequestResult.Fail(StatusBadRequest, $"At most {MaxFilters} 'filter' parameters are accepted.");
        }

        FilterParser.Result parsedFilters = FilterParser.Parse(filtersRaw, columns, measures, MaxFieldLength);

        if (parsedFilters.Outcome != FilterParser.Outcome.Ok)
        {
            return RetentionRequestResult.Fail(
                StatusBadRequest, FilterRejection.Message(parsedFilters, columns, measures, MaxFieldLength));
        }

        DateTime to = nowUtc;

        if (!string.IsNullOrEmpty(toRaw) && !RequestParsing.TryParseUtc(toRaw, out to))
        {
            return RetentionRequestResult.Fail(StatusBadRequest, "'to' must be an ISO 8601 timestamp.");
        }

        DateTime from = Start(Step(to, period, -(DefaultPeriods - 1)), period);

        if (!string.IsNullOrEmpty(fromRaw) && !RequestParsing.TryParseUtc(fromRaw, out from))
        {
            return RetentionRequestResult.Fail(StatusBadRequest, "'from' must be an ISO 8601 timestamp.");
        }

        if (from > to)
        {
            return RetentionRequestResult.Fail(StatusBadRequest, "'from' must be before or equal to 'to'.");
        }

        int periods = Count(Start(from, period), Start(to, period), period);

        if (periods > MaxPeriods)
        {
            return RetentionRequestResult.Fail(
                StatusBadRequest, $"The range spans {periods} {Wire(period)}s; at most {MaxPeriods} are accepted.");
        }

        if (!TrafficFilter.TryParse(trafficRaw, out string? trafficClass))
        {
            return RetentionRequestResult.Fail(StatusBadRequest, TrafficFilter.Rejection(trafficRaw));
        }

        return RetentionRequestResult.Ok(new RetentionRequest
        {
            ProjectId = projectId,
            TenantId = tenantId ?? string.Empty,
            TenantSet = tenantSet,
            Period = period,
            Actor = actor,
            AliasEventType = actor == RetentionActor.Identities ? aliasEventType : null,
            EventType = eventType,
            TrafficClass = trafficClass,
            Filters = parsedFilters.Filters,
            From = from,
            To = to,
            PeriodCount = periods,
        });
    }

    public static string Wire(RetentionPeriod period) => period switch
    {
        RetentionPeriod.Day => "day",
        RetentionPeriod.Month => "month",
        _ => "week",
    };

    public static string Wire(RetentionActor actor) => actor switch
    {
        RetentionActor.Sessions => "sessions",
        RetentionActor.Identities => "identities",
        _ => "users",
    };

    public static DateTime Start(DateTime value, RetentionPeriod period)
    {
        DateTime day = DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

        return period switch
        {
            RetentionPeriod.Day => day,
            RetentionPeriod.Month => new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        };
    }

    public static DateTime Step(DateTime value, RetentionPeriod period, int count) => period switch
    {
        RetentionPeriod.Day => value.AddDays(count),
        RetentionPeriod.Month => value.AddMonths(count),
        _ => value.AddDays(7 * count),
    };

    public static int Count(DateTime fromStart, DateTime toStart, RetentionPeriod period) => period switch
    {
        RetentionPeriod.Day => (int)(toStart - fromStart).TotalDays + 1,
        RetentionPeriod.Month => ((toStart.Year - fromStart.Year) * 12) + toStart.Month - fromStart.Month + 1,
        _ => ((int)(toStart - fromStart).TotalDays / 7) + 1,
    };
}

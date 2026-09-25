namespace Nealytics.Engine.Features.GetComparison;

using System;
using System.Collections.Generic;
using System.Linq;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public readonly struct ComparisonRequestResult
{
    public bool Success { get; init; }
    public int ErrorStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public ComparisonRequest Request { get; init; }

    public static ComparisonRequestResult Fail(int statusCode, string? message) =>
        new() { Success = false, ErrorStatusCode = statusCode, ErrorMessage = message };

    public static ComparisonRequestResult Ok(ComparisonRequest request) => new() { Success = true, Request = request };
}

public readonly struct ComparisonQueryParameters
{
    public string? ProjectId { get; init; }
    public string? TenantId { get; init; }
    public IReadOnlyList<string> Metrics { get; init; }
    public string? GroupBy { get; init; }
    public IReadOnlyList<string> Filters { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? PreviousFrom { get; init; }
    public string? PreviousTo { get; init; }
    public string? TimeZone { get; init; }
    public string? Traffic { get; init; }
    public string? Mode { get; init; }
    public string? Exact { get; init; }
    public string? Limit { get; init; }
    public string? OrderBy { get; init; }
    public string? Order { get; init; }
}

public static class ComparisonRequestFactory
{
    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;
    public const int DefaultLimit = 50;
    public const int MaxFilters = 16;
    public const int MaxFieldLength = RequestParsing.MaxFieldLength;

    private const string InstantRule =
        "must be an ISO 8601 timestamp; one without an offset is read in 'tz' when given, otherwise as UTC.";

    public static ComparisonRequestResult Create(
        in ComparisonQueryParameters raw,
        QueryColumns columns,
        MeasureRegistry measures,
        int maxLimit,
        int defaultRangeHours,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(measures);

        if (string.IsNullOrWhiteSpace(raw.ProjectId) || string.IsNullOrWhiteSpace(raw.TenantId))
        {
            return ComparisonRequestResult.Fail(StatusForbidden, null);
        }

        if (raw.ProjectId.Length > MaxFieldLength || raw.TenantId.Length > MaxFieldLength)
        {
            return ComparisonRequestResult.Fail(StatusBadRequest, "Project ID and Tenant ID must not exceed 256 characters.");
        }

        string[] specs = (raw.Metrics ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();

        if (specs.Length != 1)
        {
            return ComparisonRequestResult.Fail(
                StatusBadRequest, $"Exactly one 'metric' is required, in the pivot grammar. Got {specs.Length}.");
        }

        if (specs[0].Length > MaxFieldLength)
        {
            return ComparisonRequestResult.Fail(StatusBadRequest, "'metric' must not exceed 256 characters.");
        }

        if (!PivotRequestFactory.TryParseMetric(specs[0], columns, measures, out PivotMetric metric, out string? metricError))
        {
            return ComparisonRequestResult.Fail(StatusBadRequest, metricError);
        }

        string? groupBy = null;

        if (!string.IsNullOrEmpty(raw.GroupBy))
        {
            if (!columns.TryResolve(raw.GroupBy, out string resolved))
            {
                return ComparisonRequestResult.Fail(StatusBadRequest, columns.RejectionMessage("groupBy", raw.GroupBy));
            }

            groupBy = resolved;
        }

        IReadOnlyList<string> filtersRaw = raw.Filters ?? [];

        if (filtersRaw.Count > MaxFilters)
        {
            return ComparisonRequestResult.Fail(StatusBadRequest, $"At most {MaxFilters} 'filter' parameters are accepted.");
        }

        FilterParser.Result filters = FilterParser.Parse(filtersRaw, columns, measures, MaxFieldLength);

        if (filters.Outcome != FilterParser.Outcome.Ok)
        {
            return ComparisonRequestResult.Fail(
                StatusBadRequest, FilterRejection.Message(filters, columns, measures, MaxFieldLength));
        }

        if (!ZonedTime.TryResolve(raw.TimeZone, out TimeZoneInfo? zone))
        {
            return ComparisonRequestResult.Fail(
                StatusBadRequest, $"'tz' must be an IANA time zone name such as Europe/Istanbul. Got '{raw.TimeZone}'.");
        }

        DateTime from = nowUtc.AddHours(-defaultRangeHours);
        DateTime to = nowUtc;

        if (!string.IsNullOrEmpty(raw.From) && !ZonedTime.TryParseInstant(raw.From, zone, out from))
        {
            return ComparisonRequestResult.Fail(StatusBadRequest, "'from' " + InstantRule);
        }

        if (!string.IsNullOrEmpty(raw.To) && !ZonedTime.TryParseInstant(raw.To, zone, out to))
        {
            return ComparisonRequestResult.Fail(StatusBadRequest, "'to' " + InstantRule);
        }

        if (from >= to)
        {
            return ComparisonRequestResult.Fail(StatusBadRequest, "'from' must be before 'to'. A window is [from, to).");
        }

        bool hasPreviousFrom = !string.IsNullOrEmpty(raw.PreviousFrom);
        bool hasPreviousTo = !string.IsNullOrEmpty(raw.PreviousTo);

        if (hasPreviousFrom != hasPreviousTo)
        {
            return ComparisonRequestResult.Fail(
                StatusBadRequest, "'previousFrom' and 'previousTo' must be given together, or neither for the preceding window of equal length.");
        }

        (DateTime previousFrom, DateTime previousTo) = ZonedTime.Preceding(from, to, zone);

        if (hasPreviousFrom)
        {
            if (!ZonedTime.TryParseInstant(raw.PreviousFrom!, zone, out previousFrom))
            {
                return ComparisonRequestResult.Fail(StatusBadRequest, "'previousFrom' " + InstantRule);
            }

            if (!ZonedTime.TryParseInstant(raw.PreviousTo!, zone, out previousTo))
            {
                return ComparisonRequestResult.Fail(StatusBadRequest, "'previousTo' " + InstantRule);
            }

            if (previousFrom >= previousTo)
            {
                return ComparisonRequestResult.Fail(StatusBadRequest, "'previousFrom' must be before 'previousTo'.");
            }
        }

        if (!TrafficFilter.TryParse(raw.Traffic, out string? trafficClass))
        {
            return ComparisonRequestResult.Fail(StatusBadRequest, TrafficFilter.Rejection(raw.Traffic));
        }

        ComparisonOrder order = ComparisonOrder.Current;

        switch (raw.OrderBy)
        {
            case null or "" or "current":
                break;
            case "previous":
                order = ComparisonOrder.Previous;
                break;
            case "change":
                order = ComparisonOrder.Change;
                break;
            case "key":
                order = ComparisonOrder.Key;
                break;
            default:
                return ComparisonRequestResult.Fail(
                    StatusBadRequest, $"'orderBy' must be current, previous, change or key. Got '{raw.OrderBy}'.");
        }

        bool descending = order != ComparisonOrder.Key;

        switch (raw.Order)
        {
            case null or "":
                break;
            case "asc":
                descending = false;
                break;
            case "desc":
                descending = true;
                break;
            default:
                return ComparisonRequestResult.Fail(StatusBadRequest, $"'order' must be asc or desc. Got '{raw.Order}'.");
        }

        return ComparisonRequestResult.Ok(new ComparisonRequest
        {
            ProjectId = raw.ProjectId,
            TenantId = raw.TenantId,
            Metric = metric,
            GroupByColumn = groupBy,
            Filters = filters.Filters,
            TrafficClass = trafficClass,
            TimeZone = zone is null ? null : raw.TimeZone,
            From = from,
            To = to,
            PreviousFrom = previousFrom,
            PreviousTo = previousTo,
            Limit = RequestParsing.ParseLimit(raw.Limit, DefaultLimit, maxLimit),
            Order = order,
            Descending = descending,
            Approximate = string.Equals(raw.Mode, "approx", StringComparison.Ordinal),
            Exact = RequestParsing.IsFlag(raw.Exact),
        });
    }
}

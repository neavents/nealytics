namespace Nealytics.Engine.Features.GetDistribution;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public readonly struct DistributionRequestResult
{
    public bool Success { get; init; }
    public int ErrorStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DistributionRequest Request { get; init; }

    public static DistributionRequestResult Fail(int statusCode, string? message) =>
        new() { Success = false, ErrorStatusCode = statusCode, ErrorMessage = message };

    public static DistributionRequestResult Ok(DistributionRequest request) => new() { Success = true, Request = request };
}

public static class DistributionRequestFactory
{
    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;
    public const int MaxQuantiles = 8;
    public const int MaxEdges = 32;
    public const int MaxFilters = 16;
    public const int MaxFieldLength = RequestParsing.MaxFieldLength;
    public const string SessionDuration = "session_duration";
    public const string SessionEvents = "session_events";

    private static readonly double[] DefaultQuantiles = [0.5, 0.75, 0.9, 0.95];

    public static DistributionRequestResult Create(
        string? projectId,
        string? tenantId,
        string? ofRaw,
        string? eventTypeRaw,
        IReadOnlyList<string> filtersRaw,
        string? fromRaw,
        string? toRaw,
        string? quantilesRaw,
        string? bucketsRaw,
        string? trafficRaw,
        string? modeRaw,
        QueryColumns columns,
        MeasureRegistry measures,
        int defaultRangeHours,
        DateTime nowUtc) =>
        Create(
            projectId, tenantId, ofRaw, eventTypeRaw, filtersRaw, fromRaw, toRaw, quantilesRaw, bucketsRaw, trafficRaw,
            modeRaw, null, columns, measures, defaultRangeHours, nowUtc);

    public static DistributionRequestResult Create(
        string? projectId,
        string? tenantId,
        string? ofRaw,
        string? eventTypeRaw,
        IReadOnlyList<string> filtersRaw,
        string? fromRaw,
        string? toRaw,
        string? quantilesRaw,
        string? bucketsRaw,
        string? trafficRaw,
        string? modeRaw,
        string? emptyRaw,
        QueryColumns columns,
        MeasureRegistry measures,
        int defaultRangeHours,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(filtersRaw);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(measures);

        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(tenantId))
        {
            return DistributionRequestResult.Fail(StatusForbidden, null);
        }

        if (projectId.Length > MaxFieldLength || tenantId.Length > MaxFieldLength)
        {
            return DistributionRequestResult.Fail(StatusBadRequest, "Project ID and Tenant ID must not exceed 256 characters.");
        }

        DistributionSubject subject;
        string? measureColumn = null;

        switch (ofRaw)
        {
            case SessionDuration:
                subject = DistributionSubject.SessionDuration;
                break;
            case SessionEvents:
                subject = DistributionSubject.SessionEvents;
                break;
            default:
                Measure? measure = string.IsNullOrWhiteSpace(ofRaw) ? null : measures.Find(ofRaw);

                if (measure is null)
                {
                    return DistributionRequestResult.Fail(StatusBadRequest, OfRejection(ofRaw, measures));
                }

                subject = DistributionSubject.Measure;
                measureColumn = measure.Name;
                break;
        }

        string? eventType = string.IsNullOrWhiteSpace(eventTypeRaw) ? null : eventTypeRaw;

        if (eventType is not null && eventType.Length > MaxFieldLength)
        {
            return DistributionRequestResult.Fail(StatusBadRequest, "'eventType' must not exceed 256 characters.");
        }

        if (subject != DistributionSubject.Measure && eventType is not null)
        {
            return DistributionRequestResult.Fail(
                StatusBadRequest, $"'eventType' does not apply to '{ofRaw}': a session is every event it contains.");
        }

        if (filtersRaw.Count > MaxFilters)
        {
            return DistributionRequestResult.Fail(StatusBadRequest, $"At most {MaxFilters} 'filter' parameters are accepted.");
        }

        FilterParser.Result parsedFilters = FilterParser.Parse(filtersRaw, columns, measures, MaxFieldLength);

        if (parsedFilters.Outcome != FilterParser.Outcome.Ok)
        {
            return DistributionRequestResult.Fail(
                StatusBadRequest, FilterRejection.Message(parsedFilters, columns, measures, MaxFieldLength));
        }

        if (!RequestParsing.TryParseRange(fromRaw, toRaw, defaultRangeHours, nowUtc, out DateTime fromUtc, out DateTime toUtc))
        {
            return DistributionRequestResult.Fail(StatusBadRequest, "'from' and 'to' must be ISO 8601 timestamps with 'from' before or equal to 'to'.");
        }

        if (!TrafficFilter.TryParse(trafficRaw, out string? trafficClass))
        {
            return DistributionRequestResult.Fail(StatusBadRequest, TrafficFilter.Rejection(trafficRaw));
        }

        if (!EmptyCells.TryParse(emptyRaw, out bool emptyAsNull))
        {
            return DistributionRequestResult.Fail(StatusBadRequest, EmptyCells.Rejection(emptyRaw));
        }

        if (!TryParseNumbers(quantilesRaw, MaxQuantiles, out List<double> quantiles))
        {
            return DistributionRequestResult.Fail(
                StatusBadRequest, $"'quantiles' must be up to {MaxQuantiles} comma separated numbers between 0 and 1. Got '{quantilesRaw}'.");
        }

        if (quantiles.Count == 0)
        {
            quantiles.AddRange(DefaultQuantiles);
        }
        else if (quantiles.Any(q => q < 0 || q > 1))
        {
            return DistributionRequestResult.Fail(StatusBadRequest, $"'quantiles' must lie between 0 and 1. Got '{quantilesRaw}'.");
        }

        if (!TryParseNumbers(bucketsRaw, MaxEdges, out List<double> edges) || !IsStrictlyAscending(edges))
        {
            return DistributionRequestResult.Fail(
                StatusBadRequest, $"'buckets' must be up to {MaxEdges} comma separated numbers in ascending order. Got '{bucketsRaw}'.");
        }

        return DistributionRequestResult.Ok(new DistributionRequest
        {
            ProjectId = projectId,
            TenantId = tenantId,
            Subject = subject,
            MeasureColumn = measureColumn,
            Wire = ofRaw!,
            EventType = eventType,
            Filters = parsedFilters.Filters,
            TrafficClass = trafficClass,
            From = fromUtc,
            To = toUtc,
            Quantiles = quantiles,
            Edges = edges,
            Approximate = string.Equals(modeRaw, "approx", StringComparison.Ordinal),
            EmptyAsNull = emptyAsNull,
        });
    }

    private static string OfRejection(string? ofRaw, MeasureRegistry measures)
    {
        string declared = measures.Active.Count == 0
            ? "This deployment declares no measures under TelemetryEngine:Measures."
            : $"Declared measures: {string.Join(", ", measures.Active.Select(m => m.Name).Order(StringComparer.Ordinal))}.";
        return $"'of' must be a declared measure, {SessionDuration} or {SessionEvents}. Got '{ofRaw}'. {declared}";
    }

    private static bool TryParseNumbers(string? raw, int max, out List<double> values)
    {
        values = [];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            {
                return false;
            }

            values.Add(value);
        }

        return values.Count <= max;
    }

    private static bool IsStrictlyAscending(IReadOnlyList<double> values)
    {
        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] <= values[i - 1])
            {
                return false;
            }
        }

        return true;
    }
}

namespace Nealytics.Engine.Features.GetPivot;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public readonly struct PivotRequestResult
{
    public bool Success { get; init; }
    public int ErrorStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public PivotRequest Request { get; init; }

    public static PivotRequestResult Fail(int statusCode, string? message) =>
        new() { Success = false, ErrorStatusCode = statusCode, ErrorMessage = message };

    public static PivotRequestResult Ok(PivotRequest request) => new() { Success = true, Request = request };
}

public static class PivotRequestFactory
{
    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;
    public const int DefaultLimit = 50;
    public const int MaxMetrics = 12;
    public const int MaxFilters = 16;
    public const int MaxFieldLength = RequestParsing.MaxFieldLength;

    public static PivotRequestResult Create(
        string? projectId,
        string? tenantId,
        string? groupByRaw,
        IReadOnlyList<string> metricsRaw,
        IReadOnlyList<string> filtersRaw,
        string? fromRaw,
        string? toRaw,
        string? limitRaw,
        string? orderByRaw,
        string? orderRaw,
        string? trafficRaw,
        string? modeRaw,
        string? exactRaw,
        QueryColumns columns,
        MeasureRegistry measures,
        int maxLimit,
        int defaultRangeHours,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(metricsRaw);
        ArgumentNullException.ThrowIfNull(filtersRaw);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(measures);

        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(tenantId))
        {
            return PivotRequestResult.Fail(StatusForbidden, null);
        }

        if (projectId.Length > MaxFieldLength || tenantId.Length > MaxFieldLength)
        {
            return PivotRequestResult.Fail(StatusBadRequest, "Project ID and Tenant ID must not exceed 256 characters.");
        }

        if (string.IsNullOrWhiteSpace(groupByRaw) || !columns.TryResolve(groupByRaw, out string groupBy))
        {
            return PivotRequestResult.Fail(StatusBadRequest, columns.RejectionMessage("groupBy", groupByRaw));
        }

        List<string> specs = metricsRaw.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();

        if (specs.Count == 0)
        {
            return PivotRequestResult.Fail(StatusBadRequest, "At least one 'metric' is required." + MetricHint(measures));
        }

        if (specs.Count > MaxMetrics)
        {
            return PivotRequestResult.Fail(StatusBadRequest, $"At most {MaxMetrics} 'metric' parameters are accepted.");
        }

        List<PivotMetric> metrics = new(specs.Count);

        foreach (string spec in specs)
        {
            if (spec.Length > MaxFieldLength)
            {
                return PivotRequestResult.Fail(StatusBadRequest, "'metric' must not exceed 256 characters.");
            }

            if (!TryParseMetric(spec, columns, measures, out PivotMetric metric, out string? error))
            {
                return PivotRequestResult.Fail(StatusBadRequest, error);
            }

            metrics.Add(metric);
        }

        if (filtersRaw.Count > MaxFilters)
        {
            return PivotRequestResult.Fail(StatusBadRequest, $"At most {MaxFilters} 'filter' parameters are accepted.");
        }

        FilterParser.Result parsedFilters = FilterParser.Parse(filtersRaw, columns, measures, MaxFieldLength);

        if (parsedFilters.Outcome != FilterParser.Outcome.Ok)
        {
            return PivotRequestResult.Fail(
                StatusBadRequest, FilterRejection.Message(parsedFilters, columns, measures, MaxFieldLength));
        }

        if (!RequestParsing.TryParseRange(fromRaw, toRaw, defaultRangeHours, nowUtc, out DateTime fromUtc, out DateTime toUtc))
        {
            return PivotRequestResult.Fail(StatusBadRequest, "'from' and 'to' must be ISO 8601 timestamps with 'from' before or equal to 'to'.");
        }

        if (!TrafficFilter.TryParse(trafficRaw, out string? trafficClass))
        {
            return PivotRequestResult.Fail(StatusBadRequest, TrafficFilter.Rejection(trafficRaw));
        }

        bool orderByKey = false;
        int orderByMetric = 0;

        if (!string.IsNullOrEmpty(orderByRaw))
        {
            if (string.Equals(orderByRaw, "key", StringComparison.Ordinal))
            {
                orderByKey = true;
            }
            else if (!int.TryParse(orderByRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out orderByMetric)
                || orderByMetric < 0 || orderByMetric >= metrics.Count)
            {
                return PivotRequestResult.Fail(
                    StatusBadRequest, $"'orderBy' must be 'key' or the index of a metric, 0 to {metrics.Count - 1}. Got '{orderByRaw}'.");
            }
        }

        bool descending = !orderByKey;

        if (!string.IsNullOrEmpty(orderRaw))
        {
            switch (orderRaw)
            {
                case "asc":
                    descending = false;
                    break;
                case "desc":
                    descending = true;
                    break;
                default:
                    return PivotRequestResult.Fail(StatusBadRequest, $"'order' must be asc or desc. Got '{orderRaw}'.");
            }
        }

        return PivotRequestResult.Ok(new PivotRequest
        {
            ProjectId = projectId,
            TenantId = tenantId,
            GroupByColumn = groupBy,
            Metrics = metrics,
            Filters = parsedFilters.Filters,
            TrafficClass = trafficClass,
            From = fromUtc,
            To = toUtc,
            Limit = RequestParsing.ParseLimit(limitRaw, DefaultLimit, maxLimit),
            OrderByMetric = orderByMetric,
            OrderByKey = orderByKey,
            Descending = descending,
            Approximate = string.Equals(modeRaw, "approx", StringComparison.Ordinal),
            Exact = RequestParsing.IsFlag(exactRaw),
        });
    }

    public static bool TryParseMetric(
        string spec, QueryColumns columns, MeasureRegistry measures, out PivotMetric metric, out string? error)
    {
        metric = default;
        error = null;

        int scopeAt = spec.LastIndexOf(':');
        string head = scopeAt < 0 ? spec : spec[..scopeAt];
        string? eventType = scopeAt < 0 ? null : spec[(scopeAt + 1)..];

        if (eventType is not null && (eventType.Length == 0 || eventType.Length > MaxFieldLength))
        {
            error = $"'metric' scope must name an event type after the colon. Got '{spec}'.";
            return false;
        }

        switch (head)
        {
            case "events":
                metric = new PivotMetric { Kind = PivotMetricKind.Events, Wire = spec, EventType = eventType };
                return true;
            case "sessions":
                metric = new PivotMetric { Kind = PivotMetricKind.Sessions, Wire = spec, EventType = eventType };
                return true;
            case "users":
                metric = new PivotMetric { Kind = PivotMetricKind.Users, Wire = spec, EventType = eventType };
                return true;
        }

        int open = head.IndexOf('(', StringComparison.Ordinal);

        if (open <= 0 || head[^1] != ')')
        {
            error = $"'metric' must be events, sessions, users, distinct(column), or an aggregation over a "
                + $"declared measure such as avg(dwell_ms), each optionally scoped with :event_type. Got '{spec}'."
                + MetricHint(measures);
            return false;
        }

        string function = head[..open];
        string argument = head[(open + 1)..^1];

        if (string.Equals(function, "distinct", StringComparison.Ordinal))
        {
            if (!columns.TryResolve(argument, out string column))
            {
                error = columns.RejectionMessage("metric distinct()", argument);
                return false;
            }

            metric = new PivotMetric
            {
                Kind = PivotMetricKind.Distinct,
                Wire = spec,
                EventType = eventType,
                Column = column,
            };
            return true;
        }

        Measure? measure = measures.Find(argument);

        if (measure is null)
        {
            error = $"'{argument}' is not a declared measure." + MetricHint(measures);
            return false;
        }

        if (!measures.TryResolveAggregation(function, measure, out string aggregate, out string canonical))
        {
            error = $"'{function}' is not an aggregation declared for measure '{measure.Name}'. "
                + $"Declared: {string.Join(", ", measure.Aggregations.Order(StringComparer.Ordinal))}.";
            return false;
        }

        int parametersAt = aggregate.IndexOf('(', StringComparison.Ordinal);

        metric = new PivotMetric
        {
            Kind = PivotMetricKind.Measure,
            Wire = spec,
            EventType = eventType,
            Column = measure.Name,
            AggregationName = parametersAt < 0 ? aggregate : aggregate[..parametersAt],
            AggregationParameters = parametersAt < 0 ? null : aggregate[(parametersAt + 1)..^1],
            CanonicalAggregation = canonical,
        };
        return true;
    }

    private static string MetricHint(MeasureRegistry measures) =>
        measures.Active.Count == 0
            ? " This deployment declares no measures under TelemetryEngine:Measures."
            : $" Declared measures: {string.Join(", ", measures.Active.Select(m => m.Name).Order(StringComparer.Ordinal))}.";
}

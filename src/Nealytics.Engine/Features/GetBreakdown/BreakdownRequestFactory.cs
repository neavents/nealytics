namespace Nealytics.Engine.Features.GetBreakdown;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nealytics.Engine.Infrastructure.Configuration;

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
        string? trafficRaw,
        string? exactRaw,
        string? modeRaw,
        BreakdownColumns columns,
        MeasureRegistry measures,
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
        string metricWire = "events";
        string? measureColumn = null;
        string? measureFunction = null;

        if (!string.IsNullOrEmpty(metricRaw))
        {
            if (metricRaw.Length > MaxFieldLength)
            {
                return BreakdownRequestResult.Fail(
                    StatusBadRequest, "'metric' must not exceed 256 characters.");
            }

            if (TryParseMetric(metricRaw, out metric))
            {
                metricWire = ToWireFormat(metric);
            }
            else
            {
                MeasureMetricResult parsed = ParseMeasureMetric(metricRaw, measures);

                if (!parsed.Success)
                {
                    return BreakdownRequestResult.Fail(StatusBadRequest, parsed.ErrorMessage);
                }

                metric = BreakdownMetric.Measure;
                measureColumn = parsed.Column;
                measureFunction = parsed.Function;
                metricWire = parsed.Wire;
            }
        }

        if (!string.IsNullOrWhiteSpace(groupByRaw) && measures.IsActive(groupByRaw))
        {
            return BreakdownRequestResult.Fail(
                StatusBadRequest,
                $"'{groupByRaw}' is a declared measure, not a dimension. A measure is a quantity to "
                + "aggregate, so it belongs in 'metric' — try metric=avg(" + groupByRaw + "). "
                + "Grouping by it would return one row per distinct value.");
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

        // Shared with /timeseries rather than owned here, so the two endpoints cannot disagree
        // about what a filter means.
        FilterParser.Result parsedFilters = FilterParser.Parse(filtersRaw, columns, MaxFieldLength);

        switch (parsedFilters.Outcome)
        {
            case FilterParser.Outcome.Malformed:
                return BreakdownRequestResult.Fail(
                    StatusBadRequest, $"'filter' must be written name:value. Got '{parsedFilters.Offender}'.");
            case FilterParser.Outcome.UnknownColumn:
                return BreakdownRequestResult.Fail(
                    StatusBadRequest, columns.RejectionMessage("filter", parsedFilters.Column));
            case FilterParser.Outcome.ValueTooLong:
                return BreakdownRequestResult.Fail(
                    StatusBadRequest,
                    $"'filter' value for '{parsedFilters.Column}' must not exceed {MaxFieldLength} characters.");
        }

        IReadOnlyList<BreakdownFilter> filters = parsedFilters.Filters;

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

        if (!TrafficFilter.TryParse(trafficRaw, out string? trafficClass))
        {
            return BreakdownRequestResult.Fail(StatusBadRequest, TrafficFilter.Rejection(trafficRaw));
        }

        return BreakdownRequestResult.Ok(new BreakdownRequest
        {
            ProjectId = projectId,
            TenantId = tenantId,
            TrafficClass = trafficClass,
            Exact = string.Equals(exactRaw, "true", StringComparison.Ordinal),
            // Same spelling as /active's mode parameter, so the two endpoints do not disagree about
            // the word for the same idea.
            Approximate = string.Equals(modeRaw, "approx", StringComparison.Ordinal),
            Metric = metric,
            MeasureColumn = measureColumn,
            MeasureFunction = measureFunction,
            MetricWire = metricWire,
            GroupByColumn = groupByColumn,
            EventType = eventType,
            Filters = filters,
            From = fromUtc,
            To = toUtc,
            Limit = limit,
            Order = order,
        });
    }

    private readonly struct MeasureMetricResult
    {
        public bool Success { get; init; }
        public string? ErrorMessage { get; init; }
        public string Column { get; init; }
        public string Function { get; init; }
        public string Wire { get; init; }
    }

    private static MeasureMetricResult ParseMeasureMetric(string raw, MeasureRegistry measures)
    {
        int open = raw.IndexOf('(', StringComparison.Ordinal);

        if (open <= 0 || raw[^1] != ')')
        {
            return new MeasureMetricResult
            {
                ErrorMessage =
                    $"'metric' must be one of: events, sessions, users, or an aggregation over a "
                    + $"declared measure such as avg(dwell_ms). Got '{raw}'."
                    + DeclaredMeasureHint(measures),
            };
        }

        string aggregationRaw = raw[..open];
        string measureRaw = raw[(open + 1)..^1];

        Measure? measure = measures.Find(measureRaw);

        if (measure is null)
        {
            return new MeasureMetricResult
            {
                ErrorMessage =
                    $"'{measureRaw}' is not a declared measure." + DeclaredMeasureHint(measures),
            };
        }

        if (!measures.TryResolveAggregation(
                aggregationRaw, measure, out string function, out string canonicalAggregation))
        {
            return new MeasureMetricResult
            {
                ErrorMessage =
                    $"'{aggregationRaw}' is not an aggregation declared for measure "
                    + $"'{measure.Name}'. Declared: {string.Join(", ", measure.Aggregations.Order(StringComparer.Ordinal))}.",
            };
        }

        return new MeasureMetricResult
        {
            Success = true,
            Column = measure.Name,
            Function = function,
            Wire = $"{canonicalAggregation}({measure.Name})",
        };
    }

    private static string DeclaredMeasureHint(MeasureRegistry measures) =>
        measures.Active.Count == 0
            ? " This deployment declares no measures under TelemetryEngine:Measures."
            : $" Declared measures: {string.Join(", ", measures.Active.Select(m => m.Name).Order(StringComparer.Ordinal))}.";

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
        BreakdownMetric.Measure => "measure",
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    public static string ToGrain(BreakdownMetric metric) => metric switch
    {
        BreakdownMetric.Sessions => "session",
        BreakdownMetric.Users => "user",
        _ => "event",
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

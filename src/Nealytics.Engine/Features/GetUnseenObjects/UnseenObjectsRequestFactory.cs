namespace Nealytics.Engine.Features.GetUnseenObjects;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public readonly struct UnseenObjectsRequestResult
{
    public bool Success { get; init; }
    public int ErrorStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public UnseenObjectsRequest Request { get; init; }

    public static UnseenObjectsRequestResult Fail(int statusCode, string? message) =>
        new() { Success = false, ErrorStatusCode = statusCode, ErrorMessage = message };

    public static UnseenObjectsRequestResult Ok(UnseenObjectsRequest request) => new() { Success = true, Request = request };
}

public static class UnseenObjectsRequestFactory
{
    public const int StatusForbidden = 403;
    public const int StatusBadRequest = 400;
    public const int MaxIds = 5_000;
    public const int MaxFilters = 16;
    public const int MaxFieldLength = RequestParsing.MaxFieldLength;

    public static UnseenObjectsRequestResult Create(
        string? projectId,
        string? tenantId,
        UnseenObjectsBody? body,
        QueryColumns columns,
        MeasureRegistry measures,
        int defaultRangeHours,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(measures);

        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(tenantId))
        {
            return UnseenObjectsRequestResult.Fail(StatusForbidden, null);
        }

        if (projectId.Length > MaxFieldLength || tenantId.Length > MaxFieldLength)
        {
            return UnseenObjectsRequestResult.Fail(StatusBadRequest, "Project ID and Tenant ID must not exceed 256 characters.");
        }

        if (body?.Ids is not string[] raw || raw.Length == 0)
        {
            return UnseenObjectsRequestResult.Fail(StatusBadRequest, "'ids' must list at least one object id.");
        }

        if (raw.Length > MaxIds)
        {
            return UnseenObjectsRequestResult.Fail(
                StatusBadRequest, $"'ids' accepts at most {MaxIds} object ids. Got {raw.Length}.");
        }

        HashSet<string> seen = new(raw.Length, StringComparer.Ordinal);
        List<string> ids = new(raw.Length);

        for (int i = 0; i < raw.Length; i++)
        {
            string? id = raw[i];

            if (string.IsNullOrWhiteSpace(id) || id.Length > MaxFieldLength)
            {
                return UnseenObjectsRequestResult.Fail(
                    StatusBadRequest, $"'ids[{i}]' must be a non-empty object id of at most {MaxFieldLength} characters.");
            }

            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        if (!RequestParsing.TryParseRange(body.From, body.To, defaultRangeHours, nowUtc, out DateTime from, out DateTime to))
        {
            return UnseenObjectsRequestResult.Fail(
                StatusBadRequest, "'from' and 'to' must be ISO 8601 timestamps with 'from' before or equal to 'to'.");
        }

        string? eventType = string.IsNullOrWhiteSpace(body.EventType) ? null : body.EventType;
        string? impressionEventType = string.IsNullOrWhiteSpace(body.ImpressionEventType) ? null : body.ImpressionEventType;

        if (eventType?.Length > MaxFieldLength || impressionEventType?.Length > MaxFieldLength)
        {
            return UnseenObjectsRequestResult.Fail(StatusBadRequest, "Event types must not exceed 256 characters.");
        }

        if (eventType is not null && string.Equals(eventType, impressionEventType, StringComparison.Ordinal))
        {
            return UnseenObjectsRequestResult.Fail(
                StatusBadRequest, "'eventType' and 'impressionEventType' must differ; an impression cannot also be the engagement.");
        }

        string[] filtersRaw = body.Filter ?? [];

        if (filtersRaw.Length > MaxFilters)
        {
            return UnseenObjectsRequestResult.Fail(StatusBadRequest, $"At most {MaxFilters} 'filter' entries are accepted.");
        }

        FilterParser.Result filters = FilterParser.Parse(filtersRaw, columns, measures, MaxFieldLength);

        if (filters.Outcome != FilterParser.Outcome.Ok)
        {
            return UnseenObjectsRequestResult.Fail(
                StatusBadRequest, FilterRejection.Message(filters, columns, measures, MaxFieldLength));
        }

        if (!TrafficFilter.TryParse(body.Traffic, out string? trafficClass))
        {
            return UnseenObjectsRequestResult.Fail(StatusBadRequest, TrafficFilter.Rejection(body.Traffic));
        }

        return UnseenObjectsRequestResult.Ok(new UnseenObjectsRequest
        {
            ProjectId = projectId,
            TenantId = tenantId,
            Ids = ids,
            From = from,
            To = to,
            EventType = eventType,
            ImpressionEventType = impressionEventType,
            Filters = filters.Filters,
            TrafficClass = trafficClass,
        });
    }
}

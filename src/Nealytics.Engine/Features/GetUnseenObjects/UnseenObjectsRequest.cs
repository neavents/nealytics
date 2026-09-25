namespace Nealytics.Engine.Features.GetUnseenObjects;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Query;

public sealed class UnseenObjectsBody
{
    public string[]? Ids { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? EventType { get; init; }
    public string? ImpressionEventType { get; init; }
    public string[]? Filter { get; init; }
    public string? Traffic { get; init; }
}

public readonly struct UnseenObjectsRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public IReadOnlyList<string> Ids { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string? EventType { get; init; }
    public string? ImpressionEventType { get; init; }
    public IReadOnlyList<QueryFilter> Filters { get; init; }
    public string? TrafficClass { get; init; }

    public QueryScope Scope => new()
    {
        ProjectId = ProjectId,
        TenantId = TenantId,
        From = From,
        To = To,
        TrafficClass = TrafficClass,
        EventType = null,
        Filters = Filters,
    };
}

public sealed class UnseenObjectsResponse
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string? EventType { get; init; }
    public string? ImpressionEventType { get; init; }
    public string Source { get; init; } = "raw";
    public int Candidates { get; init; }
    public int Seen { get; init; }
    public IReadOnlyList<string> Unseen { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ImpressionOnly { get; init; } = Array.Empty<string>();
}

namespace Nealytics.Engine.Features.GetDistribution;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nealytics.Engine.Infrastructure.Query;

public enum DistributionSubject
{
    Measure,
    SessionDuration,
    SessionEvents,
}

public readonly struct DistributionRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public DistributionSubject Subject { get; init; }
    public string? MeasureColumn { get; init; }
    public string Wire { get; init; }
    public string? EventType { get; init; }
    public IReadOnlyList<QueryFilter> Filters { get; init; }
    public string? TrafficClass { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public IReadOnlyList<double> Quantiles { get; init; }
    public IReadOnlyList<double> Edges { get; init; }
    public bool Approximate { get; init; }
    public bool EmptyAsNull { get; init; }

    public QueryScope Scope => new()
    {
        ProjectId = ProjectId,
        TenantId = TenantId,
        From = From,
        To = To,
        TrafficClass = TrafficClass,
        EventType = EventType,
        Filters = Filters,
    };
}

public sealed class DistributionQuantile
{
    public double Q { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Value { get; init; } = 0;
}

public sealed class DistributionBucket
{
    public double? From { get; init; }
    public double? To { get; init; }
    public long Count { get; init; }
}

public sealed class DistributionResponse
{
    public string Of { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string? EventType { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string Mode { get; init; } = "exact";
    public long Count { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Min { get; init; } = 0;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Max { get; init; } = 0;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Avg { get; init; } = 0;
    public IReadOnlyList<DistributionQuantile> Quantiles { get; init; } = Array.Empty<DistributionQuantile>();
    public IReadOnlyList<DistributionBucket> Buckets { get; init; } = Array.Empty<DistributionBucket>();
}

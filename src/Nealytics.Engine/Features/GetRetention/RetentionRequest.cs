namespace Nealytics.Engine.Features.GetRetention;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Query;

public enum RetentionPeriod
{
    Day,
    Week,
    Month,
}

public enum RetentionActor
{
    Sessions,
    Users,
    Identities,
}

public readonly struct RetentionRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public TenantSet? TenantSet { get; init; }
    public RetentionPeriod Period { get; init; }
    public RetentionActor Actor { get; init; }
    public string? AliasEventType { get; init; }
    public string? EventType { get; init; }
    public string? TrafficClass { get; init; }
    public IReadOnlyList<QueryFilter> Filters { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int PeriodCount { get; init; }

    public QueryScope Scope => new()
    {
        ProjectId = ProjectId,
        TenantId = TenantId,
        TenantSet = TenantSet,
        From = From,
        To = To,
        TrafficClass = TrafficClass,
        EventType = EventType,
        Filters = Filters,
    };
}

public sealed class RetentionCohort
{
    public DateTime Cohort { get; init; }

    public long Size { get; init; }

    public long[] Returning { get; init; } = [];

    public double[] Rates { get; init; } = [];
}

public sealed class RetentionResponse
{
    public string Period { get; init; } = "week";
    public string By { get; init; } = "users";
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public IReadOnlyList<RetentionCohort> Cohorts { get; init; } = Array.Empty<RetentionCohort>();
}

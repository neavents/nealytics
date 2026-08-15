namespace Nealytics.Engine.Features.GetFunnel;

using System;
using System.Collections.Generic;

public enum FunnelGrain
{
    Sessions,
    Users,
}

public readonly struct FunnelStep
{
    public string EventType { get; init; }
    public string? FilterColumn { get; init; }
    public string? FilterValue { get; init; }
}

public readonly struct FunnelRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public IReadOnlyList<FunnelStep> Steps { get; init; }
    public FunnelGrain Grain { get; init; }
    public string? BreakdownColumn { get; init; }
    public int WindowSeconds { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int Limit { get; init; }
}

public sealed class FunnelStepResult
{
    public int Step { get; init; }
    public string Label { get; init; } = string.Empty;
    public long Count { get; init; }

    /// <summary>Share of the first step, 0..1. The first step is always 1.</summary>
    public double Conversion { get; init; }

    /// <summary>Share of the immediately preceding step, 0..1.</summary>
    public double StepConversion { get; init; }
}

public sealed class FunnelSegment
{
    public string Key { get; init; } = string.Empty;
    public IReadOnlyList<FunnelStepResult> Steps { get; init; } = Array.Empty<FunnelStepResult>();
}

public sealed class FunnelResponse
{
    public string Grain { get; init; } = "session";
    public string? BreakdownBy { get; init; }
    public int WindowSeconds { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<FunnelStepResult> Steps { get; init; } = Array.Empty<FunnelStepResult>();
    public IReadOnlyList<FunnelSegment> Segments { get; init; } = Array.Empty<FunnelSegment>();
}

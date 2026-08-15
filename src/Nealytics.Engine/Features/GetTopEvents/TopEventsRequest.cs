namespace Nealytics.Engine.Features.GetTopEvents;

using System;

public readonly struct TopEventsRequest
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string DimensionColumn { get; init; }
    public string? TrafficClass { get; init; }
    public int Limit { get; init; }

    /// <summary>
    /// Counts distinct event ids instead of rows.
    ///
    /// No query here uses FINAL, so a WAL replay leaves duplicate rows counted until a background
    /// merge collapses them. Slower and correct, opt-in, and the default is documented rather than
    /// quietly assumed.
    /// </summary>
    public bool Exact { get; init; }
}

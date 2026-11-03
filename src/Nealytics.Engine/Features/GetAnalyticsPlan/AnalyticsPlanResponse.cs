namespace Nealytics.Engine.Features.GetAnalyticsPlan;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nealytics.Engine.Infrastructure.Query;

public sealed class AnalyticsPlanResponse
{
    public string Name { get; init; } = string.Empty;
    public int Version { get; init; }
    public string Source { get; init; } = "raw";
    public string Accuracy { get; init; } = "exact";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedRollup { get; init; }
    public IReadOnlyList<AnalyticsPlanSlice> Slices { get; init; } = Array.Empty<AnalyticsPlanSlice>();
    public IReadOnlyList<AnalyticsPlanCandidate> Candidates { get; init; } = Array.Empty<AnalyticsPlanCandidate>();
}

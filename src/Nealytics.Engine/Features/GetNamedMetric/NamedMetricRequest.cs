namespace Nealytics.Engine.Features.GetNamedMetric;

using System;
using System.Text.Json.Serialization;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public sealed record NamedMetricRequest(NamedMetric Definition, QueryScope Scope);

public readonly record struct NamedMetricRequestResult(
    NamedMetricRequest? Request, int StatusCode, string? Error);

public sealed class NamedMetricResponse
{
    public string Name { get; init; } = string.Empty;
    public int Version { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Value { get; init; }
    public string? Unit { get; init; }
    public string NumericEncoding { get; init; } = "string";
    public string Source { get; init; } = "raw";
}

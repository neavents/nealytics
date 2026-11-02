namespace Nealytics.Engine.Features.GetSavedReport;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public sealed record SavedReportRequest(SavedReport Definition, QueryScope Scope);

public readonly record struct SavedReportRequestResult(
    SavedReportRequest? Request, int StatusCode, string? Error);

public sealed class SavedReportValue
{
    public string Name { get; init; } = string.Empty;
    public string Metric { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Value { get; init; }
    public string? Unit { get; init; }
}

public sealed class SavedReportResponse
{
    public string Name { get; init; } = string.Empty;
    public int Version { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string? Segment { get; init; }
    public IReadOnlyList<SavedReportValue> Fields { get; init; } = Array.Empty<SavedReportValue>();
    public string NumericEncoding { get; init; } = "string";
    public string Source { get; init; } = "raw";
}

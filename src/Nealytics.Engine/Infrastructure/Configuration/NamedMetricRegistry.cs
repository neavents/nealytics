namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public sealed class NamedMetricOptions
{
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string Aggregation { get; set; } = "count";
    public string Measure { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
}

public sealed record NamedMetric(
    string Name,
    int Version,
    string Aggregation,
    string? Measure,
    string? EventType,
    string? Unit,
    string? UnitDimension)
{
    internal bool WidenSum { get; init; }
}

public sealed class NamedMetricRegistry
{
    private static readonly Regex NamePattern = new(
        "^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled);

    private readonly FrozenDictionary<string, NamedMetric> _byName;

    public NamedMetricRegistry(TelemetryEngineOptions options, MeasureRegistry measures)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(measures);
        if (options.MaxNamedMetrics is < 1 or > 1024)
        {
            throw new InvalidOperationException("TelemetryEngine:MaxNamedMetrics must be between 1 and 1024.");
        }
        List<NamedMetricOptions> configured = options.NamedMetrics ?? [];
        if (configured.Count > options.MaxNamedMetrics)
        {
            throw new InvalidOperationException("TelemetryEngine:NamedMetrics exceeds MaxNamedMetrics.");
        }

        List<NamedMetric> definitions = new(configured.Count);
        HashSet<string> names = new(StringComparer.Ordinal);
        for (int index = 0; index < configured.Count; index++)
        {
            NamedMetricOptions declaration = configured[index]
                ?? throw Invalid(index, "declaration is null");
            string? name = declaration.Name?.Trim();
            if (name is null || !NamePattern.IsMatch(name))
            {
                throw Invalid(index, "name must match [a-z][a-z0-9_]{0,62}");
            }
            if (!names.Add(name))
            {
                throw Invalid(index, "name is duplicated");
            }
            if (declaration.Version != 1)
            {
                throw Invalid(index, "only version 1 is supported");
            }
            string? aggregation = declaration.Aggregation?.Trim().ToLowerInvariant();
            if (aggregation is not ("count" or "sum" or "avg" or "min" or "max"))
            {
                throw Invalid(index, "aggregation must be count, sum, avg, min or max");
            }
            string? column = Normalize(declaration.Measure);
            Measure? quantity = column is null ? null : measures.Find(column);
            if (column is not null && quantity is null)
            {
                throw Invalid(index, "measure is not active");
            }
            if (quantity is null && aggregation != "count")
            {
                throw Invalid(index, "aggregation requires a measure");
            }
            if (quantity is not null && !measures.TryResolveAggregation(aggregation, quantity, out _, out _))
            {
                throw Invalid(index, "aggregation is not permitted by the measure");
            }
            string? eventType = Normalize(declaration.EventType);
            string? unit = Normalize(declaration.Unit);
            if (eventType?.Length > 256 || unit?.Length > 256)
            {
                throw Invalid(index, "event type and unit must not exceed 256 characters");
            }
            definitions.Add(new NamedMetric(name, 1, aggregation, column, eventType, unit,
                aggregation == "count" ? null : quantity!.UnitDimension)
            {
                WidenSum = aggregation == "sum" && quantity!.Kind is not
                    (MeasureValueKind.Float32 or MeasureValueKind.Float64 or MeasureValueKind.Decimal)
            });
        }
        Declared = Array.AsReadOnly(definitions.ToArray());
        _byName = definitions.ToFrozenDictionary(definition => definition.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<NamedMetric> Declared { get; }

    public NamedMetric? Find(string? name) =>
        name is not null && _byName.TryGetValue(name, out NamedMetric? definition) ? definition : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static InvalidOperationException Invalid(int index, string reason) =>
        new InvalidOperationException($"TelemetryEngine:NamedMetrics[{index}]: {reason}.");
}

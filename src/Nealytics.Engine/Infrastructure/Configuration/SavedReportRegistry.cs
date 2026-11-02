namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public sealed class SavedReportFieldOptions
{
    public string Name { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
}

public sealed class SavedReportOptions
{
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string Segment { get; set; } = string.Empty;
    public List<SavedReportFieldOptions> Fields { get; set; } = [];
}

public sealed record SavedReportField(string Name, string Metric)
{
    internal NamedMetric Definition { get; init; } = null!;
}

public sealed record SavedReport(
    string Name, int Version, string? Segment, IReadOnlyList<SavedReportField> Fields);

public sealed class SavedReportRegistry
{
    private static readonly Regex NamePattern = new(
        "^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled);

    private readonly FrozenDictionary<string, SavedReport> _byName;

    public SavedReportRegistry(
        TelemetryEngineOptions options, NamedMetricRegistry metrics, EventSegmentRegistry segments)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(segments);
        if (options.MaxSavedReports is < 1 or > 1024)
        {
            throw new InvalidOperationException("TelemetryEngine:MaxSavedReports must be between 1 and 1024.");
        }
        List<SavedReportOptions> configured = options.SavedReports ?? [];
        if (configured.Count > options.MaxSavedReports)
        {
            throw new InvalidOperationException("TelemetryEngine:SavedReports exceeds MaxSavedReports.");
        }

        List<SavedReport> definitions = new(configured.Count);
        HashSet<string> names = new(StringComparer.Ordinal);
        for (int index = 0; index < configured.Count; index++)
        {
            SavedReportOptions declaration = configured[index]
                ?? throw Invalid(index, "declaration is null");
            string name = ReadName(declaration.Name, index, "name");
            if (!names.Add(name))
            {
                throw Invalid(index, "name is duplicated");
            }
            if (declaration.Version != 1)
            {
                throw Invalid(index, "only version 1 is supported");
            }
            string? segment = string.IsNullOrWhiteSpace(declaration.Segment) ? null : declaration.Segment.Trim();
            if (segment is not null && segments.Find(segment) is null)
            {
                throw Invalid(index, "segment is not declared");
            }
            List<SavedReportFieldOptions>? configuredFields = declaration.Fields;
            if (configuredFields is null || configuredFields.Count is < 1 or > 16)
            {
                throw Invalid(index, "provide between 1 and 16 fields");
            }
            List<SavedReportField> fields = new(configuredFields.Count);
            HashSet<string> fieldNames = new(StringComparer.Ordinal);
            for (int fieldIndex = 0; fieldIndex < configuredFields.Count; fieldIndex++)
            {
                SavedReportFieldOptions field = configuredFields[fieldIndex]
                    ?? throw Invalid(index, $"fields[{fieldIndex}] is null");
                string fieldName = ReadName(field.Name, index, "field name");
                if (!fieldNames.Add(fieldName))
                {
                    throw Invalid(index, "field name is duplicated");
                }
                NamedMetric? metric = metrics.Find(field.Metric?.Trim());
                if (metric is null)
                {
                    throw Invalid(index, $"fields[{fieldIndex}] metric is not declared");
                }
                fields.Add(new SavedReportField(fieldName, metric.Name) { Definition = metric });
            }
            definitions.Add(new SavedReport(name, 1, segment, Array.AsReadOnly(fields.ToArray())));
        }
        Declared = Array.AsReadOnly(definitions.ToArray());
        _byName = definitions.ToFrozenDictionary(definition => definition.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<SavedReport> Declared { get; }

    public SavedReport? Find(string? name) =>
        name is not null && _byName.TryGetValue(name, out SavedReport? definition) ? definition : null;

    private static string ReadName(string? value, int index, string label)
    {
        string? name = value?.Trim();
        if (name is null || !NamePattern.IsMatch(name))
        {
            throw Invalid(index, label + " must match [a-z][a-z0-9_]{0,62}");
        }
        return name;
    }

    private static InvalidOperationException Invalid(int index, string reason) =>
        new InvalidOperationException($"TelemetryEngine:SavedReports[{index}]: {reason}.");
}

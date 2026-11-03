namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Nealytics.Engine.Infrastructure.Query;

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
    public List<string> GroupBy { get; set; } = [];
    public string Interval { get; set; } = string.Empty;
    public int Limit { get; set; } = 100;
    public List<ReportParameterOptions> Parameters { get; set; } = [];
}

public sealed record SavedReportField(string Name, string Metric)
{
    internal NamedMetric Definition { get; init; } = null!;
}

public sealed record SavedReport(
    string Name, int Version, string? Segment, IReadOnlyList<SavedReportField> Fields)
{
    public IReadOnlyList<string> GroupBy { get; init; } = Array.Empty<string>();
    public string? Interval { get; init; }
    public int Limit { get; init; } = 100;
    public IReadOnlyList<ReportParameter> Parameters { get; init; } = Array.Empty<ReportParameter>();
    internal bool IsGrouped => GroupBy.Count > 0 || Interval is not null;
}

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

        DimensionRegistry dimensions = new(options);
        QueryColumns columns = new(dimensions);
        MeasureRegistry measures = new(options, dimensions);
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
            if (new MetricProjection(fields.Select(field => field.Definition)).Components.Count > 128)
            {
                throw Invalid(index, "report aggregate projection exceeds 128 components");
            }
            IReadOnlyList<string> groups = CompileGroups(declaration.GroupBy, columns, index);
            string? interval = string.IsNullOrWhiteSpace(declaration.Interval) ? null : declaration.Interval.Trim().ToLowerInvariant();
            if (interval is not (null or "hour" or "day"))
            {
                throw Invalid(index, "interval must be hour or day");
            }
            bool grouped = groups.Count > 0 || interval is not null;
            int projected = new MetricProjection(fields.Select(field => field.Definition)).Components.Count;
            if (declaration.Limit < 1 || declaration.Limit > Math.Min(options.MaxQueryLimit, 10_000)
                || grouped && declaration.Limit * Math.Max(fields.Count, projected) > 16_384)
            {
                throw Invalid(index, "grouped report exceeds its row or 16384-cell limit");
            }
            IReadOnlyList<ReportParameter> parameters;
            try
            {
                parameters = ReportParameterCompiler.Compile(declaration.Parameters, columns, measures);
            }
            catch (InvalidOperationException error)
            {
                throw Invalid(index, error.Message);
            }
            definitions.Add(new SavedReport(name, 1, segment, Array.AsReadOnly(fields.ToArray()))
            {
                GroupBy = groups,
                Interval = interval,
                Limit = declaration.Limit,
                Parameters = parameters
            });
        }
        Declared = Array.AsReadOnly(definitions.ToArray());
        _byName = definitions.ToFrozenDictionary(definition => definition.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<SavedReport> Declared { get; }

    public SavedReport? Find(string? name) =>
        name is not null && _byName.TryGetValue(name, out SavedReport? definition) ? definition : null;

    private static IReadOnlyList<string> CompileGroups(List<string>? configured, QueryColumns columns, int index)
    {
        if (configured is null || configured.Count > 2)
        {
            throw Invalid(index, "provide a nonnull list with at most two group columns");
        }
        string[] groups = new string[configured.Count];
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int position = 0; position < configured.Count; position++)
        {
            if (!columns.TryResolve(configured[position]?.Trim(), out string column) || !seen.Add(column))
            {
                throw Invalid(index, "group columns must be unique active event columns");
            }
            groups[position] = column;
        }
        return Array.AsReadOnly(groups);
    }

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

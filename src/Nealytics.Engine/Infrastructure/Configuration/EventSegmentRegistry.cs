namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Nealytics.Engine.Infrastructure.Query;

public sealed class EventSegmentOptions
{
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public List<string> Filters { get; set; } = [];
}

public sealed record EventSegment(string Name, int Version, IReadOnlyList<string> Filters)
{
    internal IReadOnlyList<QueryFilter> CompiledFilters { get; init; } = [];
}

public sealed class EventSegmentRegistry
{
    private static readonly Regex NamePattern = new(
        "^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled);

    private readonly FrozenDictionary<string, EventSegment> _byName;

    public EventSegmentRegistry(TelemetryEngineOptions options, QueryColumns columns, MeasureRegistry measures)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(measures);
        if (options.MaxEventSegments is < 1 or > 1024)
        {
            throw new InvalidOperationException("TelemetryEngine:MaxEventSegments must be between 1 and 1024.");
        }
        List<EventSegmentOptions> configured = options.EventSegments ?? [];
        if (configured.Count > options.MaxEventSegments)
        {
            throw new InvalidOperationException("TelemetryEngine:EventSegments exceeds MaxEventSegments.");
        }

        List<EventSegment> definitions = new(configured.Count);
        HashSet<string> names = new(StringComparer.Ordinal);
        for (int index = 0; index < configured.Count; index++)
        {
            EventSegmentOptions declaration = configured[index]
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
            List<string>? filters = declaration.Filters;
            if (filters is null || filters.Count is < 1 or > 32 || filters.Any(string.IsNullOrWhiteSpace))
            {
                throw Invalid(index, "provide between 1 and 32 nonblank filters");
            }
            FilterParser.Result parsed = FilterParser.Parse(filters, columns, measures, 256);
            if (parsed.Outcome != FilterParser.Outcome.Ok)
            {
                throw Invalid(index, FilterRejection.Message(parsed, columns, measures, 256));
            }
            if (parsed.Filters.Any(filter => TenantAttributeRegistry.IsGroupColumn(filter.Column)))
            {
                throw Invalid(index, "tenant attributes are not event fields");
            }
            definitions.Add(new EventSegment(name, 1, Array.AsReadOnly(filters.ToArray()))
            {
                CompiledFilters = Array.AsReadOnly(parsed.Filters.ToArray())
            });
        }
        Declared = Array.AsReadOnly(definitions.ToArray());
        _byName = definitions.ToFrozenDictionary(definition => definition.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<EventSegment> Declared { get; }

    public EventSegment? Find(string? name) =>
        name is not null && _byName.TryGetValue(name, out EventSegment? definition) ? definition : null;

    private static InvalidOperationException Invalid(int index, string reason) =>
        new InvalidOperationException($"TelemetryEngine:EventSegments[{index}]: {reason}.");
}

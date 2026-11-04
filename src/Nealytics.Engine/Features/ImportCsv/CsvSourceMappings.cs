namespace Nealytics.Engine.Features.ImportCsv;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Nealytics.Engine.Features.SourceMapping;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

internal sealed class CsvProjectMapping
{
    private readonly SourceRecordMapping _mapping;
    private readonly string[] _columns;

    public CsvProjectMapping(SourceMappingOptions declaration)
    {
        _mapping = SourceRecordMapping.Compile(declaration);
        if (declaration.Fields.Values.Any(field => field.Path.Length > 1))
        {
            throw new InvalidOperationException("CSV bindings require literal single-column paths.");
        }
        _columns = declaration.Fields.Values.Where(field => field.Path.Length == 1)
            .Select(field => field.Path[0]).Distinct(StringComparer.Ordinal).ToArray();
    }

    public FrozenDictionary<string, int>? BindHeader(IReadOnlyList<string> cells)
    {
        Dictionary<string, int> columns = new(StringComparer.Ordinal);
        for (int index = 0; index < cells.Count; index++)
        {
            string name = cells[index];
            if (name.Length is < 1 or > 128 || !columns.TryAdd(name, index))
            {
                return null;
            }
        }
        return _columns.All(columns.ContainsKey) ? columns.ToFrozenDictionary(StringComparer.Ordinal) : null;
    }

    public GlobalTelemetryPayload Read(
        IReadOnlyDictionary<string, int> columns, IReadOnlyList<string> cells, int textLimit) =>
        _mapping.Read(columns, cells, textLimit);
}

internal sealed class CsvSourceMappings
{
    private readonly FrozenDictionary<string, CsvProjectMapping> _projects;

    public CsvSourceMappings(TelemetryEngineOptions options, ProjectCatalog projects)
    {
        if (options.CsvSourceMappings is null || options.CsvSourceMappings.Count > 128)
        {
            throw new InvalidOperationException("CSV accepts at most 128 project source mappings.");
        }
        Dictionary<string, CsvProjectMapping> mappings = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, SourceMappingOptions> entry in options.CsvSourceMappings)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 256
                || projects.Resolve(entry.Key) is null || entry.Value is null)
            {
                throw new InvalidOperationException("CSV source mappings require configured project contexts and declarations.");
            }
            mappings.Add(entry.Key, new(entry.Value));
        }
        _projects = mappings.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public CsvProjectMapping? Find(string project) => _projects.GetValueOrDefault(project);
}

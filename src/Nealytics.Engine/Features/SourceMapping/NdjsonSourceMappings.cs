namespace Nealytics.Engine.Features.SourceMapping;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Configuration;

internal sealed class NdjsonSourceMappings
{
    private readonly Dictionary<string, SourceRecordMapping> _projects = new(StringComparer.Ordinal);

    public NdjsonSourceMappings(TelemetryEngineOptions options, ProjectCatalog projects)
    {
        if (options.NdjsonSourceMappings is null || options.NdjsonSourceMappings.Count > 128)
        {
            throw new InvalidOperationException("NDJSON accepts at most 128 project source mappings.");
        }
        foreach (KeyValuePair<string, SourceMappingOptions> entry in options.NdjsonSourceMappings)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 256
                || projects.Resolve(entry.Key) is null || entry.Value is null)
            {
                throw new InvalidOperationException("NDJSON source mappings require configured project contexts and declarations.");
            }
            _projects.Add(entry.Key, SourceRecordMapping.Compile(entry.Value));
        }
    }

    public SourceRecordMapping? Find(string project) => _projects.GetValueOrDefault(project);
}

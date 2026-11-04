namespace Nealytics.Engine.Features.ImportJobs;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

internal sealed class ImportJobContracts
{
    private readonly Dictionary<string, string> _projects = new(StringComparer.Ordinal);
    private readonly string _default;

    public ImportJobContracts(TelemetryEngineOptions options, ProjectCatalog projects, ImportJobLimits limits)
    {
        _default = Fingerprint(projects.Default, null, options, limits);
        foreach (ProjectContext project in projects.Declared)
        {
            _projects.Add(project.ProjectId!, Fingerprint(project, null, options, limits));
        }
        foreach (KeyValuePair<string, SourceMappingOptions> mapping in options.NdjsonSourceMappings)
        {
            _projects[mapping.Key] = Fingerprint(projects.Resolve(mapping.Key)!, mapping.Value, options, limits);
        }
    }

    public string Find(string project) => _projects.GetValueOrDefault(project, _default);

    private static string Fingerprint(ProjectContext project, SourceMappingOptions? mapping,
        TelemetryEngineOptions options, ImportJobLimits limits)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("import-contract-v1");
            writer.WriteStringValue(TelemetryDeliverySchema.Fingerprint(new(project.Dimensions, project.Measures)));
            writer.WriteStringValue(options.ServerEventTypes);
            writer.WriteStringValue(options.AliasEventType);
            writer.WriteNumberValue(options.ImportMaxLineBytes);
            writer.WriteNumberValue(options.ImportMaxRecords);
            writer.WriteNumberValue(limits.ChunkBytes);
            foreach (Measure measure in project.Measures.Active)
            {
                writer.WriteStringValue(measure.UnitDimension);
                writer.WriteBooleanValue(measure.ServerOnly);
            }
            SourceMappingOptions? ordered = mapping is null ? null : new()
            {
                Fields = mapping.Fields.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
            };
            JsonSerializer.Serialize(writer, ordered, TelemetryAotContext.Default.SourceMappingOptions);
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }
}

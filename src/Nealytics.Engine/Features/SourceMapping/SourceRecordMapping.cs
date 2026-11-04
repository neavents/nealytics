namespace Nealytics.Engine.Features.SourceMapping;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

internal sealed class SourceRecordMapping
{
    private static readonly HashSet<string> TextTargets = new(StringComparer.Ordinal)
    {
        "eventId", "timestamp", "projectId", "tenantId", "eventType", "sessionId", "userId",
        "objectId", "trafficClass", "pagePath", "referrer", "deviceClass", "os", "browser",
        "country", "metadataJson"
    };
    private readonly SourceField[] _fields;

    private SourceRecordMapping(SourceField[] fields) => _fields = fields;

    public static SourceRecordMapping Compile(SourceMappingOptions options)
    {
        if (options.Fields is null || options.Fields.Count is < 5 or > 64)
        {
            throw new InvalidOperationException("Source mapping requires 5–64 field bindings.");
        }
        Dictionary<string, SourceField> fields = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, SourceFieldOptions> entry in options.Fields)
        {
            string target = entry.Key;
            bool scalar = target.StartsWith("dimensions.", StringComparison.Ordinal)
                || target.StartsWith("measures.", StringComparison.Ordinal);
            SourceFieldOptions field = entry.Value;
            if ((!TextTargets.Contains(target) && !scalar) || target.Length > 128
                || (scalar && target.EndsWith('.')) || field is null || field.Path is null
                || field.Path.Length > 16 || field.Default?.Length > 4_096
                || (field.Path.Length == 0 && field.Default is null))
            {
                throw new InvalidOperationException("Source mapping contains an invalid field declaration.");
            }
            foreach (string segment in field.Path)
            {
                if (string.IsNullOrEmpty(segment) || segment.Length > 128)
                {
                    throw new InvalidOperationException("Source mapping paths require 1–128 character object keys.");
                }
            }
            fields.Add(target, new SourceField(target, (string[])field.Path.Clone(), field.Default, scalar));
        }
        foreach (string required in new[] { "eventId", "timestamp", "projectId", "tenantId", "eventType" })
        {
            if (!fields.TryGetValue(required, out SourceField? field)
                || (required is "eventId" or "timestamp" && (field.Path.Length == 0 || field.Default is not null)))
            {
                throw new InvalidOperationException("Source mapping requires explicit identity/time paths and project, tenant and event bindings.");
            }
        }
        SourceField[] snapshot = new SourceField[fields.Count];
        fields.Values.CopyTo(snapshot, 0);
        return new SourceRecordMapping(snapshot);
    }

    public GlobalTelemetryPayload Read(JsonElement root, int textByteLimit)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Source mapping requires a JSON object.");
        }
        return Read(new JsonSourceValues(root), textByteLimit);
    }

    public GlobalTelemetryPayload Read(
        IReadOnlyDictionary<string, int> columns, IReadOnlyList<string> cells, int textByteLimit) =>
        Read(new ColumnSourceValues(columns, cells), textByteLimit);

    private GlobalTelemetryPayload Read<T>(T source, int textByteLimit) where T : struct, ISourceValues
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        Dictionary<string, string> dimensions = new(StringComparer.Ordinal);
        Dictionary<string, string> measures = new(StringComparer.Ordinal);
        int remaining = textByteLimit;
        foreach (SourceField field in _fields)
        {
            string? value = source.Read(field);
            if (value is null)
            {
                continue;
            }
            int bytes = Encoding.UTF8.GetByteCount(value);
            if (bytes > remaining)
            {
                throw new InvalidDataException("Source mapping exceeds its text byte budget.");
            }
            remaining -= bytes;
            if (field.Target.StartsWith("dimensions.", StringComparison.Ordinal))
            {
                dimensions.Add(field.Target[11..], value);
            }
            else if (field.Target.StartsWith("measures.", StringComparison.Ordinal))
            {
                measures.Add(field.Target[9..], value);
            }
            else
            {
                values.Add(field.Target, value);
            }
        }
        Guid eventId = Guid.Empty;
        DateTime timestamp = default;
        if (!Guid.TryParse(values.GetValueOrDefault("eventId"), out eventId) || eventId == Guid.Empty
            || !DateTime.TryParseExact(values.GetValueOrDefault("timestamp"), "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp) || timestamp.Year is < 1900 or > 2299)
        {
            throw new InvalidDataException("Source mapping requires a nonzero eventId and UTC-Z timestamp in the storage calendar.");
        }
        return new GlobalTelemetryPayload
        {
            EventId = eventId,
            Timestamp = timestamp,
            ProjectId = values.GetValueOrDefault("projectId", string.Empty),
            TenantId = values.GetValueOrDefault("tenantId", string.Empty),
            EventType = values.GetValueOrDefault("eventType", string.Empty),
            SessionId = values.GetValueOrDefault("sessionId", string.Empty),
            UserId = values.GetValueOrDefault("userId"),
            ObjectId = values.GetValueOrDefault("objectId"),
            TrafficClass = values.GetValueOrDefault("trafficClass", string.Empty),
            PagePath = values.GetValueOrDefault("pagePath", string.Empty),
            Referrer = values.GetValueOrDefault("referrer", string.Empty),
            DeviceClass = values.GetValueOrDefault("deviceClass", string.Empty),
            Os = values.GetValueOrDefault("os", string.Empty),
            Browser = values.GetValueOrDefault("browser", string.Empty),
            Country = values.GetValueOrDefault("country", string.Empty),
            MetadataJson = values.GetValueOrDefault("metadataJson", "{}"),
            Dimensions = dimensions,
            Measures = measures
        };
    }
}

namespace Nealytics.Engine.Features.KafkaIngestion;

using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

internal sealed class KafkaRecordParser
{
    private readonly KafkaConnectorSettings _settings;
    private readonly IngestGate _gate;
    private readonly DimensionSanitizer _dimensions;
    private readonly MeasureSanitizer _measures;

    public KafkaRecordParser(KafkaConnectorSettings settings, TelemetryEngineOptions options,
        ILoggerFactory logging)
    {
        _settings = settings;
        _gate = new IngestGate(options, settings.Context.Measures);
        _dimensions = new DimensionSanitizer(settings.Context.Dimensions, logging.CreateLogger<DimensionSanitizer>());
        _measures = new MeasureSanitizer(settings.Context.Measures, logging.CreateLogger<MeasureSanitizer>());
    }

    public GlobalTelemetryPayload Parse(byte[]? value, DateTime receivedAt)
    {
        if (value is null || value.Length == 0 || value.Length > _settings.MaxMessageBytes)
        {
            throw new InvalidDataException("Kafka record is empty, a tombstone or exceeds MaxMessageBytes.");
        }
        GlobalTelemetryPayload payload = null!;
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("eventId", out JsonElement identity)
                || identity.ValueKind != JsonValueKind.String
                || !identity.TryGetGuid(out Guid eventId) || eventId == Guid.Empty
                || !root.TryGetProperty("timestamp", out JsonElement time)
                || time.ValueKind != JsonValueKind.String
                || !time.GetString()!.EndsWith('Z')
                || !time.TryGetDateTime(out DateTime timestamp)
                || timestamp.Year is < 1900 or > 2299)
            {
                throw new InvalidDataException("Kafka record requires an explicit nonzero eventId and UTC timestamp in the storage calendar.");
            }
            payload = root.Deserialize(TelemetryRequestJson.Payload)!;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Kafka record is not a valid telemetry JSON object.");
        }
        IngestRejection rejection = _gate.Check(_settings.Policy, payload, receivedAt, out _);
        if (rejection != IngestRejection.None)
        {
            throw new InvalidDataException("Kafka record rejected by ingestion policy: " + IngestValidation.Tag(rejection));
        }
        _dimensions.Sanitize(payload);
        _measures.Sanitize(payload);
        return payload;
    }
}

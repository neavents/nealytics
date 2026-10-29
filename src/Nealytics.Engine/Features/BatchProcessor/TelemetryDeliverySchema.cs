namespace Nealytics.Engine.Features.BatchProcessor;

using System;
using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Nealytics.Engine.Infrastructure.Configuration;

internal static class TelemetryDeliverySchema
{
    public static string Fingerprint(TelemetryColumnLayout layout)
    {
        ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("delivery-schema-v1");
            writer.WriteStartArray();
            foreach (string column in layout.ColumnNames)
            {
                writer.WriteStringValue(column);
            }
            writer.WriteEndArray();
            writer.WriteStartArray();
            foreach (Dimension dimension in layout.Dimensions)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(dimension.ClickHouseType);
                writer.WriteNumberValue((int)dimension.Kind);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray();
            foreach (Measure measure in layout.Measures)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(measure.ClickHouseType);
                writer.WriteNumberValue((int)measure.Kind);
                writer.WriteStringValue(measure.Minimum?.ToString(CultureInfo.InvariantCulture));
                writer.WriteStringValue(measure.Maximum?.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }
}

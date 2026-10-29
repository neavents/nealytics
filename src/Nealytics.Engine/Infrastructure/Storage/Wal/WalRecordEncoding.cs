namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System.Buffers;
using System.Text.Json;
using Nealytics.Engine.Infrastructure.Serialization;

internal static class WalRecordEncoding
{
    public static JsonWriterOptions WriterOptions => new JsonWriterOptions { SkipValidation = true };

    public static void Serialize(GlobalTelemetryPayload payload, IBufferWriter<byte> buffer)
    {
        using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            JsonSerializer.Serialize(writer, payload, TelemetryAotContext.Default.GlobalTelemetryPayload);
        }
        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);
    }
}

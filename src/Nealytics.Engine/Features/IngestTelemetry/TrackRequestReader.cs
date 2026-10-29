namespace Nealytics.Engine.Features.IngestTelemetry;

using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Serialization;

internal static class TrackRequestReader
{
    public static async Task<GlobalTelemetryPayload?> ReadAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = result.Buffer;
                if (result.IsCompleted)
                {
                    Utf8JsonReader json = new Utf8JsonReader(buffer);
                    return JsonSerializer.Deserialize(ref json, TelemetryAotContext.Default.GlobalTelemetryPayload);
                }
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }
}

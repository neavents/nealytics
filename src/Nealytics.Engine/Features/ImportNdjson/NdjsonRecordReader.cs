namespace Nealytics.Engine.Features.ImportNdjson;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Features.SourceMapping;

internal sealed record NdjsonRecord(GlobalTelemetryPayload? Payload, long WireBytes, string? Error);

internal static class NdjsonRecordReader
{
    internal static async IAsyncEnumerable<NdjsonRecord> ReadAsync(
        PipeReader reader, int lineLimit, [EnumeratorCancellation] CancellationToken cancellationToken,
        ArrayPool<byte>? bufferPool = null, SourceRecordMapping? mapping = null)
    {
        ArrayPool<byte> pool = bufferPool ?? ArrayPool<byte>.Shared;
        byte[]? partial = null;
        int partialLength = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadResult result = await reader.ReadAsync(cancellationToken);
                if (result.IsCanceled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                ReadOnlySequence<byte> buffer = result.Buffer;
                if (buffer.IsEmpty && result.IsCompleted && partialLength == 0)
                {
                    yield break;
                }
                SequencePosition? newline = buffer.PositionOf((byte)'\n');
                ReadOnlySequence<byte> line = newline is null ? buffer : buffer.Slice(0, newline.Value);
                if (line.Length > lineLimit - partialLength)
                {
                    reader.AdvanceTo(buffer.End);
                    yield return new(null, 0, "line_too_large");
                    yield break;
                }
                if (newline is null && !result.IsCompleted)
                {
                    if (!line.IsEmpty)
                    {
                        partial ??= pool.Rent(lineLimit);
                        line.CopyTo(partial.AsSpan(partialLength));
                        partialLength += (int)line.Length;
                    }
                    reader.AdvanceTo(buffer.End);
                    continue;
                }
                long bytes = partialLength + line.Length + (newline is null ? 0 : 1);
                NdjsonRecord record = null!;
                if (partialLength == 0)
                {
                    record = Parse(line, bytes, mapping, lineLimit);
                }
                else
                {
                    line.CopyTo(partial!.AsSpan(partialLength));
                    record = Parse(new ReadOnlySequence<byte>(partial.AsMemory(0, partialLength + (int)line.Length)),
                        bytes, mapping, lineLimit);
                    partialLength = 0;
                }
                reader.AdvanceTo(newline is null ? buffer.End : buffer.GetPosition(1, newline.Value));
                yield return record;
                if (record.Error is not null)
                {
                    yield break;
                }
            }
        }
        finally
        {
            try
            {
                await reader.CompleteAsync();
            }
            finally
            {
                if (partial is not null)
                {
                    pool.Return(partial, clearArray: true);
                }
            }
        }
    }

    private static NdjsonRecord Parse(ReadOnlySequence<byte> line, long bytes, SourceRecordMapping? mapping, int lineLimit)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (mapping is not null)
            {
                return new(mapping.Read(root, lineLimit), bytes, null);
            }
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new(null, 0, "invalid_record");
            }
            if (!root.TryGetProperty("eventId", out JsonElement identity) || identity.ValueKind != JsonValueKind.String
                || !identity.TryGetGuid(out Guid eventId) || eventId == Guid.Empty)
            {
                return new(null, 0, "explicit_event_id_required");
            }
            if (!root.TryGetProperty("timestamp", out JsonElement time) || time.ValueKind != JsonValueKind.String
                || !time.TryGetDateTime(out DateTime timestamp) || timestamp.Kind != DateTimeKind.Utc)
            {
                return new(null, 0, "explicit_utc_timestamp_required");
            }
            if (timestamp.Year is < 1900 or > 2299)
            {
                return new(null, 0, "timestamp_out_of_range");
            }
            GlobalTelemetryPayload? payload = root.Deserialize(TelemetryRequestJson.Payload);
            return new(payload, bytes, null);
        }
        catch (JsonException)
        {
            return new(null, 0, "invalid_json");
        }
        catch (InvalidDataException)
        {
            return new(null, 0, "invalid_mapping");
        }
    }
}

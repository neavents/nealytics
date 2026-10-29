namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Serialization;

internal sealed class WalReplayReader
{
    private readonly int _recordBytes;
    private readonly ArrayPool<byte> _pool;

    internal static FileOptions OpenOptions => FileOptions.Asynchronous | FileOptions.SequentialScan;

    public WalReplayReader(int recordBytes, ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recordBytes, 2);
        _recordBytes = recordBytes;
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    public async IAsyncEnumerable<WalReplayRecord> ReadAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        int readBytes = Math.Min(_recordBytes, 65_536);
        byte[] input = _pool.Rent(readBytes);
        try
        {
            using WalRecordBuffer record = new WalRecordBuffer(_recordBytes - 1, _pool);
            bool first = true;
            long offset = 0;
            int count;
            while ((count = await source.ReadAsync(input.AsMemory(0, readBytes), cancellationToken)) != 0)
            {
                int consumed = 0;
                while (consumed != count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int newline = Array.IndexOf(input, (byte)'\n', consumed, count - consumed);
                    int length = (newline < 0 ? count : newline) - consumed;
                    Append(record, input.AsSpan(consumed, length), offset);
                    if (newline < 0)
                    {
                        break;
                    }

                    int framedBytes = record.WrittenMemory.Length + 1;
                    GlobalTelemetryPayload? payload = Decode(record.WrittenMemory.Span, first, offset);
                    first = false;
                    offset += framedBytes;
                    record.Reset();
                    consumed = newline + 1;
                    if (payload is not null)
                    {
                        yield return new WalReplayRecord(payload, framedBytes);
                    }
                }
            }
        }
        finally
        {
            _pool.Return(input, clearArray: true);
        }
    }

    private void Append(WalRecordBuffer record, ReadOnlySpan<byte> bytes, long offset)
    {
        if (bytes.Length > _recordBytes - 1 - record.WrittenMemory.Length)
        {
            throw new InvalidDataException("WAL frame at byte " + offset + " exceeds WalMaxRecordBytes.");
        }
        if (!bytes.IsEmpty)
        {
            bytes.CopyTo(record.GetSpan(bytes.Length));
            record.Advance(bytes.Length);
        }
    }

    private static GlobalTelemetryPayload? Decode(ReadOnlySpan<byte> bytes, bool first, long offset)
    {
        if (first && bytes.StartsWith("\uFEFF"u8))
        {
            bytes = bytes[3..];
        }
        if (IsBlank(bytes))
        {
            return null;
        }

        try
        {
            Guid identity = ReadIdentity(bytes);
            GlobalTelemetryPayload? payload = JsonSerializer.Deserialize(bytes, TelemetryAotContext.Default.GlobalTelemetryPayload);
            if (payload is null || identity == Guid.Empty || payload.EventId != identity
                || string.IsNullOrEmpty(payload.ProjectId)
                || string.IsNullOrEmpty(payload.TenantId)
                || string.IsNullOrEmpty(payload.EventType))
            {
                throw new InvalidDataException("WAL frame at byte " + offset + " lacks stable identity or required fields.");
            }
            return payload;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Malformed WAL frame at byte " + offset + ".", error);
        }
    }

    private static bool IsBlank(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
            {
                return false;
            }
        }
        return true;
    }

    private static Guid ReadIdentity(ReadOnlySpan<byte> bytes)
    {
        Utf8JsonReader reader = new Utf8JsonReader(bytes);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1
                || !reader.ValueTextEquals("eventId"u8))
            {
                continue;
            }
            reader.Read();
            return reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out Guid value)
                ? value
                : Guid.Empty;
        }
        return Guid.Empty;
    }
}

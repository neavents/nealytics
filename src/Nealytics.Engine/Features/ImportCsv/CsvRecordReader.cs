namespace Nealytics.Engine.Features.ImportCsv;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Storage.Wal;

internal sealed record CsvRecord(string[]? Cells, long WireBytes, string? Error);

internal static class CsvRecordReader
{
    private static readonly Encoding StrictUtf8 = Encoding.GetEncoding(Encoding.UTF8.CodePage,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    public static async IAsyncEnumerable<CsvRecord> ReadAsync(
        PipeReader reader, int recordLimit, [EnumeratorCancellation] CancellationToken cancellationToken,
        ArrayPool<byte>? pool = null)
    {
        using WalRecordBuffer record = new(recordLimit, pool);
        CsvFramer framer = new(recordLimit);
        bool first = true;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadResult read = await reader.ReadAsync(cancellationToken);
                if (read.IsCanceled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                ReadOnlySequence<byte> buffer = read.Buffer;
                if (buffer.IsEmpty && read.IsCompleted && record.WrittenMemory.IsEmpty)
                {
                    yield break;
                }
                long consumed = framer.Scan(buffer);
                if (framer.Error is not null)
                {
                    reader.AdvanceTo(buffer.GetPosition(consumed));
                    yield return new(null, 0, framer.Error);
                    yield break;
                }
                if (consumed != 0)
                {
                    buffer.Slice(0, consumed).CopyTo(record.GetSpan((int)consumed));
                    record.Advance((int)consumed);
                }
                reader.AdvanceTo(buffer.GetPosition(consumed));
                if (!framer.Complete && !read.IsCompleted)
                {
                    continue;
                }
                CsvRecord decoded = framer.Complete || framer.ValidEnd
                    ? Decode(record.WrittenMemory.Span, framer.Commas, first)
                    : new(null, 0, "invalid_csv");
                yield return decoded;
                if (decoded.Error is not null)
                {
                    yield break;
                }
                first = false;
                record.Reset();
                framer.Reset();
            }
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private static CsvRecord Decode(ReadOnlySpan<byte> raw, IReadOnlyList<int> commas, bool first)
    {
        long wireBytes = raw.Length;
        int start = first && raw.StartsWith("\uFEFF"u8) ? 3 : 0;
        if (raw.EndsWith("\n"u8))
        {
            raw = raw[..^1];
            if (raw.EndsWith("\r"u8))
            {
                raw = raw[..^1];
            }
        }
        try
        {
            string[] cells = new string[commas.Count + 1];
            for (int index = 0; index < cells.Length; index++)
            {
                int end = index == commas.Count ? raw.Length : commas[index];
                ReadOnlySpan<byte> field = raw[start..end];
                cells[index] = field.StartsWith("\""u8)
                    ? StrictUtf8.GetString(field[1..^1]).Replace("\"\"", "\"")
                    : StrictUtf8.GetString(field);
                start = end + 1;
            }
            return new(cells, wireBytes, null);
        }
        catch (DecoderFallbackException)
        {
            return new(null, 0, "invalid_utf8");
        }
    }
}

namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

internal sealed class LengthLimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private long _read;

    public LengthLimitedStream(Stream inner, long limit)
    {
        _inner = inner;
        _limit = limit;
    }

    private int Count(int justRead)
    {
        _read += justRead;

        if (_read > _limit)
        {
            throw new BadHttpRequestException(
                $"Request body exceeded {_limit} bytes.", StatusCodes.Status413PayloadTooLarge);
        }

        return justRead;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Count(_inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

internal sealed class BoundedRequestPipeReader : PipeReader
{
    private readonly PipeReader _inner;
    private readonly long _limit;
    private ReadOnlySequence<byte> _buffer;
    private long _retained;
    private long _observed;
    private bool _reading;
    private bool _completed;

    public BoundedRequestPipeReader(PipeReader inner, long limit)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        _inner = inner;
        _limit = limit;
    }

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        return Observe(await _inner.ReadAsync(cancellationToken));
    }

    public override bool TryRead(out ReadResult result)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (!_inner.TryRead(out result))
        {
            return false;
        }
        result = Observe(result);
        return true;
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        _retained = _buffer.Slice(consumed).Length;
        _inner.AdvanceTo(consumed, examined);
        _buffer = default;
        _reading = false;
    }

    public override void CancelPendingRead() => _inner.CancelPendingRead();

    public override void Complete(Exception? exception = null)
    {
        if (_completed)
        {
            return;
        }
        _completed = true;
        if (_reading)
        {
            _inner.AdvanceTo(_buffer.End);
        }
        _buffer = default;
    }

    private ReadResult Observe(ReadResult result)
    {
        _buffer = result.Buffer;
        _reading = true;
        long added = _buffer.Length - _retained;
        if (added > _limit - _observed)
        {
            throw new BadHttpRequestException(
                $"Request body exceeded {_limit} bytes.", StatusCodes.Status413PayloadTooLarge);
        }
        _observed += added;
        return result;
    }
}

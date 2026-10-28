namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.Buffers;
using System.IO;

internal sealed class WalRecordBuffer : IBufferWriter<byte>, IDisposable
{
    private readonly int _limit;
    private readonly ArrayPool<byte> _pool;
    private byte[] _buffer = Array.Empty<byte>();
    private int _written;

    public WalRecordBuffer(int limit, ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        _limit = limit;
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public void Reset() => _written = 0;

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Math.Min(_buffer.Length, _limit) - _written);
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        int required = Math.Max(sizeHint, 1);
        if (required > _limit - _written)
        {
            throw new InvalidDataException("Serialized WAL record exceeds WalMaxRecordBytes.");
        }

        if (required > _buffer.Length - _written)
        {
            int capacity = (int)Math.Min(_limit, Math.Max((long)_written + required, (long)_buffer.Length * 2));
            byte[] replacement = _pool.Rent(capacity);
            _buffer.AsSpan(0, _written).CopyTo(replacement);
            ReturnBuffer();
            _buffer = replacement;
        }

        return _buffer.AsMemory(_written, Math.Min(_buffer.Length, _limit) - _written);
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public void Dispose()
    {
        ReturnBuffer();
        _buffer = Array.Empty<byte>();
        _written = 0;
    }

    private void ReturnBuffer()
    {
        if (_buffer.Length > 0)
        {
            _pool.Return(_buffer, clearArray: true);
        }
    }
}

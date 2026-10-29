namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.IO;

internal sealed class WalBacklogBudget
{
    private readonly object _sync = new object();
    private readonly long _capacity;
    private long _bytes;

    public WalBacklogBudget(long capacity, long existingBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(existingBytes);
        _capacity = capacity;
        _bytes = existingBytes;
    }

    public long Bytes
    {
        get { lock (_sync) { return _bytes; } }
    }

    public void Reserve(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        lock (_sync)
        {
            if (bytes > _capacity - _bytes)
            {
                throw new IOException("The durable WAL backlog reached WalMaxBacklogBytes; retry after storage drains.");
            }
            _bytes += bytes;
        }
    }

    public void Release(long bytes)
    {
        lock (_sync)
        {
            if (bytes < 0 || bytes > _bytes)
            {
                throw new InvalidOperationException("The WAL backlog release exceeds its retained bytes.");
            }
            _bytes -= bytes;
        }
    }
}

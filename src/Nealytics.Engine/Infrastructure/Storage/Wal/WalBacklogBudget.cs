namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.IO;

internal sealed class WalBacklogBudget
{
    private readonly object _sync = new object();
    private readonly long _capacity;
    private readonly long _recordCapacity;
    private long _bytes;
    private long _records;

    public WalBacklogBudget(
        long capacity,
        long existingBytes,
        long recordCapacity = long.MaxValue,
        long existingRecords = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(existingBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(recordCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(existingRecords);
        _recordCapacity = recordCapacity;
        _records = existingRecords;
        _capacity = capacity;
        _bytes = existingBytes;
    }

    public long Bytes
    {
        get { lock (_sync) { return _bytes; } }
    }

    public void Reserve(int bytes, int records = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(records, 1);
        lock (_sync)
        {
            if (bytes > _capacity - _bytes || records > _recordCapacity - _records)
            {
                throw new IOException("The durable WAL backlog reached WalMaxBacklogBytes; retry after storage drains.");
            }
            _bytes += bytes;
            _records += records;
        }
    }

    public void Release(long bytes, long records = 0)
    {
        lock (_sync)
        {
            if (bytes < 0 || bytes > _bytes || records < 0 || records > _records)
            {
                throw new InvalidOperationException("The WAL backlog release exceeds its retained bytes.");
            }
            _bytes -= bytes;
            _records -= records;
        }
    }
}

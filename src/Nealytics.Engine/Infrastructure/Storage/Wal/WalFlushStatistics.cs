namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.Threading;

internal sealed class WalFlushStatistics
{
    private long _groups;
    private long _records;
    private long _elapsedTicks;

    public long GroupCount => Interlocked.Read(ref _groups);
    public long RecordCount => Interlocked.Read(ref _records);

    public double AverageGroupSize
    {
        get
        {
            long groups = GroupCount;
            return groups == 0 ? 0 : (double)RecordCount / groups;
        }
    }

    public double AverageFlushMilliseconds
    {
        get
        {
            long groups = GroupCount;
            return groups == 0 ? 0 : (double)Interlocked.Read(ref _elapsedTicks) / TimeSpan.TicksPerMillisecond / groups;
        }
    }

    public void Record(int records, TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(records, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        Interlocked.Add(ref _records, records);
        Interlocked.Add(ref _elapsedTicks, elapsed.Ticks);
        Interlocked.Increment(ref _groups);
    }
}

namespace Nealytics.Engine.Bench.Measurements;

using System.Numerics;

public sealed class LatencyHistogram
{
    private const int SubBuckets = 256;
    private const int MaximumExponent = 40;
    private const long MaximumMicroseconds = 1L << MaximumExponent;

    private readonly long[] _counts = new long[MaximumExponent * SubBuckets + 2];
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public double MaximumMilliseconds => Percentile(100);

    public void Record(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0
            || milliseconds > MaximumMicroseconds / 1000.0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }

        long microseconds = (long)Math.Ceiling(milliseconds * 1000);
        int index = 0;

        if (microseconds > 0)
        {
            int exponent = BitOperations.Log2((ulong)microseconds);
            long basis = 1L << exponent;
            int subBucket = (int)((microseconds - basis) * SubBuckets / basis);
            index = 1 + exponent * SubBuckets + subBucket;
        }

        Interlocked.Increment(ref _counts[index]);
        Interlocked.Increment(ref _count);
    }

    public double Percentile(double percentile)
    {
        if (!double.IsFinite(percentile) || percentile < 0 || percentile > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        long count = Count;

        if (count == 0)
        {
            return 0;
        }

        long rank = Math.Max(1, (long)Math.Ceiling(count * (percentile / 100)));
        long cumulative = Interlocked.Read(ref _counts[0]);
        int index = 0;

        while (cumulative < rank)
        {
            index++;
            cumulative += Interlocked.Read(ref _counts[index]);
        }

        if (index == 0)
        {
            return 0;
        }

        int exponent = (index - 1) / SubBuckets;
        int subBucket = (index - 1) % SubBuckets;
        double basis = Math.ScaleB(1, exponent);
        double upperMicroseconds = basis + (subBucket + 1) * basis / SubBuckets;
        return Math.Min(MaximumMicroseconds, upperMicroseconds) / 1000;
    }
}

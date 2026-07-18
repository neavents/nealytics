namespace Nealytics.Engine.Features.BatchProcessor;

using System;

public static class TelemetryInsertMath
{
    public const int CrossBatchBackoffCeilingMs = 30_000;

    public static int ComputeCrossBatchBackoffMs(int consecutiveFailures)
    {
        if (consecutiveFailures < 0)
        {
            consecutiveFailures = 0;
        }
        if (consecutiveFailures >= 30)
        {
            return CrossBatchBackoffCeilingMs;
        }
        return Math.Min(1000 * (1 << consecutiveFailures), CrossBatchBackoffCeilingMs);
    }

    public static int ComputeRetryBackoffMs(int attempt, int ceilingMs)
    {
        if (attempt < 1)
        {
            attempt = 1;
        }
        int exponent = attempt - 1;
        if (exponent >= 30)
        {
            return ceilingMs;
        }
        return Math.Min(1000 * (1 << exponent), ceilingMs);
    }

    public static DateTimeOffset ToClickHouseTimestamp(DateTime timestamp)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc), TimeSpan.Zero);
    }
}

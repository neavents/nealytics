using FluentAssertions;
using Nealytics.Engine.Features.BatchProcessor;

namespace Nealytics.Engine.Tests.Unit;

public class TelemetryInsertMathTests
{
    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 2000)]
    [InlineData(2, 4000)]
    [InlineData(3, 8000)]
    [InlineData(4, 16000)]
    [InlineData(5, 30000)]
    [InlineData(10, 30000)]
    public void ComputeCrossBatchBackoffMs_ExponentialWithCeiling(int failures, int expected)
    {
        TelemetryInsertMath.ComputeCrossBatchBackoffMs(failures).Should().Be(expected);
    }

    [Fact]
    public void ComputeCrossBatchBackoffMs_NegativeTreatedAsZero()
    {
        TelemetryInsertMath.ComputeCrossBatchBackoffMs(-3).Should().Be(1000);
    }

    [Fact]
    public void ComputeCrossBatchBackoffMs_HugeFailureCount_DoesNotOverflow()
    {
        TelemetryInsertMath.ComputeCrossBatchBackoffMs(int.MaxValue)
            .Should().Be(TelemetryInsertMath.CrossBatchBackoffCeilingMs);
    }

    [Theory]
    [InlineData(1, 30000, 1000)]
    [InlineData(2, 30000, 2000)]
    [InlineData(3, 30000, 4000)]
    [InlineData(6, 30000, 30000)]
    public void ComputeRetryBackoffMs_ExponentialWithCeiling(int attempt, int ceiling, int expected)
    {
        TelemetryInsertMath.ComputeRetryBackoffMs(attempt, ceiling).Should().Be(expected);
    }

    [Fact]
    public void ComputeRetryBackoffMs_RespectsLowCeiling()
    {
        TelemetryInsertMath.ComputeRetryBackoffMs(5, 3000).Should().Be(3000);
    }

    [Fact]
    public void ComputeRetryBackoffMs_AttemptBelowOne_TreatedAsFirstAttempt()
    {
        TelemetryInsertMath.ComputeRetryBackoffMs(0, 30000).Should().Be(1000);
    }

    [Fact]
    public void ComputeRetryBackoffMs_HugeAttempt_DoesNotOverflow()
    {
        TelemetryInsertMath.ComputeRetryBackoffMs(int.MaxValue, 12345).Should().Be(12345);
    }

    [Fact]
    public void ToClickHouseTimestamp_ForcesUtcWithZeroOffset()
    {
        DateTime unspecified = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Unspecified);

        DateTimeOffset result = TelemetryInsertMath.ToClickHouseTimestamp(unspecified);

        result.Offset.Should().Be(TimeSpan.Zero);
        result.DateTime.Should().Be(unspecified);
        result.UtcDateTime.Should().Be(new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ToClickHouseTimestamp_PreservesWallClock_RegardlessOfInputKind()
    {
        DateTime asUtc = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc);
        DateTime asLocal = DateTime.SpecifyKind(asUtc, DateTimeKind.Local);

        TelemetryInsertMath.ToClickHouseTimestamp(asUtc).UtcDateTime
            .Should().Be(TelemetryInsertMath.ToClickHouseTimestamp(asLocal).UtcDateTime);
    }
}

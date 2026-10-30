using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalFlushStatisticsTests
{
    [Fact]
    public void EmptyStatisticsHaveNoInventedDurationOrRecords()
    {
        WalFlushStatistics statistics = new();
        Assert.Equal(0, statistics.GroupCount);
        Assert.Equal(0, statistics.RecordCount);
        Assert.Equal(0, statistics.AverageGroupSize);
        Assert.Equal(0, statistics.AverageFlushMilliseconds);
    }

    [Fact]
    public void AveragesUseAllGroupsAndReportMilliseconds()
    {
        WalFlushStatistics statistics = new();
        statistics.Record(3, TimeSpan.FromMilliseconds(20));
        statistics.Record(7, TimeSpan.FromMilliseconds(80));
        Assert.Equal(2, statistics.GroupCount);
        Assert.Equal(10, statistics.RecordCount);
        Assert.Equal(5, statistics.AverageGroupSize);
        Assert.Equal(50, statistics.AverageFlushMilliseconds);
        statistics.Record(1, TimeSpan.Zero);
        Assert.Equal(11d / 3, statistics.AverageGroupSize);
        Assert.Equal(100d / 3, statistics.AverageFlushMilliseconds);
    }

    [Fact]
    public void SubmillisecondMeasurementsRetainTheirFractionalValue()
    {
        WalFlushStatistics statistics = new();
        statistics.Record(1, TimeSpan.FromTicks(15_000));
        Assert.Equal(1.5, statistics.AverageFlushMilliseconds);
    }

    [Fact]
    public void InvalidMeasurementsDoNotChangeTheTotals()
    {
        WalFlushStatistics statistics = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => statistics.Record(0, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => statistics.Record(-1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => statistics.Record(1, TimeSpan.FromTicks(-1)));
        Assert.Equal(0, statistics.GroupCount);
        Assert.Equal(0, statistics.RecordCount);
    }

    [Fact]
    public async Task ConcurrentFlushesAreAllCounted()
    {
        WalFlushStatistics statistics = new();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(
            () => statistics.Record(2, TimeSpan.FromMilliseconds(10)))));
        Assert.Equal(100, statistics.GroupCount);
        Assert.Equal(200, statistics.RecordCount);
        Assert.Equal(2, statistics.AverageGroupSize);
        Assert.Equal(10, statistics.AverageFlushMilliseconds);
    }
}

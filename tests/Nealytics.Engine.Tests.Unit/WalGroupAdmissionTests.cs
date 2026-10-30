using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalGroupAdmissionTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(5, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 11)]
    public async Task InvalidGroupsCannotReserveAnyBudget(int records, int bytes)
    {
        WalAdmission admission = new(4, 10);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            admission.ReserveAsync(records, bytes, CancellationToken.None).AsTask());
        Assert.Equal(0, admission.PendingRecords);
        Assert.Equal(0, admission.PendingBytes);
    }

    [Fact]
    public async Task GroupsAndSingleRecordsShareTheSameLogicalBudget()
    {
        WalAdmission admission = new(4, 10);
        await admission.ReserveAsync(3, 6, CancellationToken.None);
        await admission.ReserveAsync(4, CancellationToken.None);
        Assert.Equal(4, admission.PendingRecords);
        Assert.Equal(10, admission.PendingBytes);
        admission.Release(4);
        Assert.Equal(3, admission.PendingRecords);
        Assert.Equal(6, admission.PendingBytes);
        admission.Release(3, 6);
        Assert.Equal(0, admission.PendingRecords);
        Assert.Equal(0, admission.PendingBytes);
    }

    [Theory]
    [InlineData(3, 4, 2, 2)]
    [InlineData(2, 9, 1, 2)]
    public async Task AGroupWaitsUntilItsEntireRecordAndByteReservationFits(int firstCount, int firstBytes, int nextCount, int nextBytes)
    {
        WalAdmission admission = new(4, 10);
        await admission.ReserveAsync(firstCount, firstBytes, CancellationToken.None);
        Task waiting = admission.ReserveAsync(nextCount, nextBytes, CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(firstCount, admission.PendingRecords);
        Assert.Equal(firstBytes, admission.PendingBytes);
        admission.Release(firstCount, firstBytes);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(nextCount, admission.PendingRecords);
        Assert.Equal(nextBytes, admission.PendingBytes);
        admission.Release(nextCount, nextBytes);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(3, 5)]
    [InlineData(2, 0)]
    [InlineData(2, -1)]
    [InlineData(2, 6)]
    public async Task InvalidGroupReleasesLeaveEveryReservationIntact(int records, int bytes)
    {
        WalAdmission admission = new(4, 10);
        await admission.ReserveAsync(2, 5, CancellationToken.None);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => admission.Release(records, bytes));
        Assert.Equal("WAL admission release exceeds the reserved budget.", error.Message);
        Assert.Equal(2, admission.PendingRecords);
        Assert.Equal(5, admission.PendingBytes);
        admission.Release(2, 5);
    }

    [Fact]
    public async Task CancelingAWaitingGroupCannotPartiallyConsumeTheRemainingCapacity()
    {
        WalAdmission admission = new(4, 10);
        await admission.ReserveAsync(3, 6, CancellationToken.None);
        using CancellationTokenSource canceled = new();
        Task waiting = admission.ReserveAsync(2, 2, canceled.Token).AsTask();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(3, admission.PendingRecords);
        Assert.Equal(6, admission.PendingBytes);
        await admission.ReserveAsync(4, CancellationToken.None);
        admission.Release(4);
        admission.Release(3, 6);
    }

    [Fact]
    public async Task ClosingAdmissionFaultsAWaitingGroupWithoutChangingItsOwnersBudget()
    {
        WalAdmission admission = new(4, 10);
        await admission.ReserveAsync(3, 6, CancellationToken.None);
        Task waiting = admission.ReserveAsync(2, 2, CancellationToken.None).AsTask();
        IOException failure = new("terminal storage failure");
        admission.Close(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => waiting));
        Assert.Equal(3, admission.PendingRecords);
        Assert.Equal(6, admission.PendingBytes);
        admission.Release(3, 6);
        Assert.Equal(0, admission.PendingRecords);
        Assert.Equal(0, admission.PendingBytes);
    }

    [Fact]
    public async Task ExactMaximumIntegerBudgetsDoNotOverflowWhenAnotherGroupWaits()
    {
        WalAdmission admission = new(int.MaxValue, int.MaxValue);
        await admission.ReserveAsync(int.MaxValue, int.MaxValue, CancellationToken.None);
        Task waiting = admission.ReserveAsync(1, 1, CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(int.MaxValue, admission.PendingRecords);
        Assert.Equal(int.MaxValue, admission.PendingBytes);
        admission.Release(int.MaxValue, int.MaxValue);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, admission.PendingRecords);
        Assert.Equal(1, admission.PendingBytes);
        admission.Release(1, 1);
    }
}

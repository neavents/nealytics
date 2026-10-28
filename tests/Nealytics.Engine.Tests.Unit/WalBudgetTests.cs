using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalBudgetTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void AdmissionRequiresPositiveLimits(int records, int bytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WalAdmission(records, bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public async Task ReservationMustFitTheByteBudget(int bytes)
    {
        WalAdmission admission = new(2, 10);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => admission.ReserveAsync(bytes, CancellationToken.None).AsTask());
        Assert.Equal(0, admission.PendingRecords);
        Assert.Equal(0, admission.PendingBytes);
    }

    [Theory]
    [InlineData(1, 10, 4, 4)]
    [InlineData(2, 10, 7, 4)]
    [InlineData(2, 10, 10, 1)]
    public async Task ReservationWaitsForEitherRecordOrByteCapacity(
        int recordLimit, int byteLimit, int firstBytes, int nextBytes)
    {
        WalAdmission admission = new(recordLimit, byteLimit);
        await admission.ReserveAsync(firstBytes, CancellationToken.None);
        Task pending = admission.ReserveAsync(nextBytes, CancellationToken.None).AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, admission.PendingRecords);
        Assert.Equal(firstBytes, admission.PendingBytes);

        admission.Release(firstBytes);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, admission.PendingRecords);
        Assert.Equal(nextBytes, admission.PendingBytes);
        admission.Release(nextBytes);
        Assert.Equal(0, admission.PendingRecords);
        Assert.Equal(0, admission.PendingBytes);
    }

    [Fact]
    public async Task ExactCapacityAndMultipleReservationsAreAccountedTogether()
    {
        WalAdmission admission = new(3, 10);
        await admission.ReserveAsync(4, CancellationToken.None);
        await admission.ReserveAsync(6, CancellationToken.None);
        Assert.Equal(2, admission.PendingRecords);
        Assert.Equal(10, admission.PendingBytes);
        admission.Release(4);
        await admission.ReserveAsync(4, CancellationToken.None);
        Assert.Equal(2, admission.PendingRecords);
        Assert.Equal(10, admission.PendingBytes);
        admission.Release(6);
        admission.Release(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDoesNotTakeOrLeakAReservation(bool alreadyFull)
    {
        WalAdmission admission = new(1, 10);
        using CancellationTokenSource cancellation = new();
        if (alreadyFull)
        {
            await admission.ReserveAsync(5, CancellationToken.None);
        }
        else
        {
            cancellation.Cancel();
        }

        Task pending = admission.ReserveAsync(5, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(alreadyFull ? 1 : 0, admission.PendingRecords);
        Assert.Equal(alreadyFull ? 5 : 0, admission.PendingBytes);
        if (alreadyFull)
        {
            admission.Release(5);
        }
        await admission.ReserveAsync(10, CancellationToken.None);
        Assert.Equal(10, admission.PendingBytes);
    }

    [Fact]
    public async Task ClosingWakesWaitersAndPreservesTheFirstFailure()
    {
        WalAdmission admission = new(1, 10);
        IOException failure = new("uncertain write");
        await admission.ReserveAsync(10, CancellationToken.None);
        Task waiter = admission.ReserveAsync(1, CancellationToken.None).AsTask();

        admission.Close(failure);
        admission.Close(new InvalidOperationException("later failure"));

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => waiter));
        admission.Release(10);
        Assert.Equal(0, admission.PendingBytes);
        Assert.Equal(0, admission.PendingRecords);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(
            () => admission.ReserveAsync(1, CancellationToken.None).AsTask()));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(6, true)]
    public async Task InvalidReleaseCannotCorruptTheBudget(int bytes, bool reserved)
    {
        WalAdmission admission = new(2, 10);
        if (reserved)
        {
            await admission.ReserveAsync(5, CancellationToken.None);
        }
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => admission.Release(bytes));
        Assert.Contains("reserved budget", error.Message);
        Assert.Equal(reserved ? 1 : 0, admission.PendingRecords);
        Assert.Equal(reserved ? 5 : 0, admission.PendingBytes);
    }

    [Fact]
    public async Task ConcurrentReservationsNeverExceedEitherLimit()
    {
        WalAdmission admission = new(3, 12);
        int inside = 0;
        Task[] workers = Enumerable.Range(0, 50).Select(async _ =>
        {
            await admission.ReserveAsync(4, CancellationToken.None);
            Assert.InRange(Interlocked.Increment(ref inside), 1, 3);
            Assert.InRange(admission.PendingBytes, 4, 12);
            await Task.Yield();
            Interlocked.Decrement(ref inside);
            admission.Release(4);
        }).ToArray();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, admission.PendingRecords);
        Assert.Equal(0, admission.PendingBytes);
    }

    [Fact]
    public void LimitsPreserveEveryConfiguredCapacity()
    {
        WalLimits limits = new(new TelemetryEngineOptions
        {
            WalMaxPendingRecords = 7,
            WalMaxPendingBytes = 4096,
            WalMaxRecordBytes = 1024,
            WalMaxFlushRecords = 3,
            WalMaxFlushBytes = 2048
        });
        Assert.Equal(7, limits.PendingRecords);
        Assert.Equal(4096, limits.PendingBytes);
        Assert.Equal(1024, limits.RecordBytes);
        Assert.Equal(3, limits.FlushRecords);
        Assert.Equal(2048, limits.FlushBytes);
    }

    [Theory]
    [InlineData(0, 4096, 1024, 3, 2048)]
    [InlineData(7, 255, 256, 3, 256)]
    [InlineData(7, 4096, 255, 3, 2048)]
    [InlineData(7, 4096, 2049, 3, 2048)]
    [InlineData(7, 4096, 1024, 3, 4097)]
    [InlineData(7, 4096, 1024, 0, 2048)]
    [InlineData(7, 4096, 1024, 8, 2048)]
    public void InconsistentLimitsAreRejectedBeforeOpeningStorage(
        int records, int bytes, int recordBytes, int flushRecords, int flushBytes)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => new WalLimits(new TelemetryEngineOptions
        {
            WalMaxPendingRecords = records,
            WalMaxPendingBytes = bytes,
            WalMaxRecordBytes = recordBytes,
            WalMaxFlushRecords = flushRecords,
            WalMaxFlushBytes = flushBytes
        }));
        Assert.Equal("options", error.ParamName);
        Assert.Contains("record <= flush <= pending", error.Message);
    }

    [Fact]
    public void EqualMinimumAndMaximumLimitsAreValid()
    {
        WalLimits limits = new(new TelemetryEngineOptions
        {
            WalMaxPendingRecords = 1,
            WalMaxPendingBytes = 256,
            WalMaxRecordBytes = 256,
            WalMaxFlushRecords = 1,
            WalMaxFlushBytes = 256
        });
        Assert.Equal(256, limits.RecordBytes);
        Assert.Equal(1, limits.FlushRecords);
    }

    [Theory]
    [InlineData(-1, 65_536)]
    [InlineData(0, 65_536)]
    [InlineData(1, 1)]
    [InlineData(4096, 4096)]
    public void ManagedFileBufferRetainsItsCompatibleFallback(int requested, int expected)
    {
        WalLimits limits = new(new TelemetryEngineOptions { WalFileBufferBytes = requested });
        Assert.Equal(expected, limits.FileBufferBytes);
    }
}

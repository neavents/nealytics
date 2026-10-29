using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalEncodingReservationTests
{
    [Theory]
    [InlineData("x", 2_200)]
    [InlineData("\"", 1_100)]
    [InlineData("\\", 1_100)]
    [InlineData("λ", 1_200)]
    [InlineData("🙂", 1_000)]
    [InlineData("\u0001", 500)]
    public void ExactFramedLimitsAcceptQuotedEscapedAndMultibyteValues(string token, int repetitions)
    {
        GlobalTelemetryPayload payload = Event(string.Concat(Enumerable.Repeat(token, repetitions)));
        byte[] expected = Frame(payload);
        TrackingPool pool = new();
        using (WalRecordBuffer buffer = new(expected.Length, pool, reserveForEncoding: true))
        {
            WalRecordEncoding.Serialize(payload, buffer);
            Assert.Equal(expected, buffer.WrittenMemory.ToArray());
            Assert.All(pool.Requests, size => Assert.InRange(size, 1, expected.Length * 6 + 4096));
        }
        Assert.Equal(0, pool.Outstanding);
        Assert.All(pool.Returned, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        using WalRecordBuffer tooSmall = new(expected.Length - 1, reserveForEncoding: true);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => WalRecordEncoding.Serialize(payload, tooSmall));
        Assert.Equal("Serialized WAL record exceeds WalMaxRecordBytes.", error.Message);
        Assert.True(tooSmall.WrittenMemory.Length < expected.Length);
    }

    [Fact]
    public async Task ActualOutputLimitsApplyThroughDurableAppendAndRecovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-encoding-reservation-" + Guid.NewGuid().ToString("N"));
        GlobalTelemetryPayload payload = Event(new string('x', 2_200));
        byte[] expected = Frame(payload);
        TelemetryEngineOptions settings = new()
        {
            WriteAheadLogDirectory = directory,
            WalMaxRecordBytes = expected.Length,
            WalMaxFlushBytes = expected.Length,
            WalMaxPendingBytes = expected.Length * 2
        };
        try
        {
            await using (WriteAheadLogger wal = new(Options.Create(settings)))
            {
                await wal.AppendAsync(payload, CancellationToken.None);
                Assert.Equal(1, wal.UncommittedRecordCount);
                Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(directory, "telemetry_wal.log")));
            }
            await using WriteAheadLogger restarted = new(Options.Create(settings));
            Assert.Equal(payload.EventId, Assert.Single(await restarted.ReplayUncommittedAsync()).EventId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void ScratchReservationDoesNotIncreaseTheCommittedOutputBudget()
    {
        TrackingPool pool = new();
        using WalRecordBuffer buffer = new(4, pool, reserveForEncoding: true);
        Memory<byte> reservation = buffer.GetMemory(4_120);
        Assert.Equal(4_120, reservation.Length);
        Assert.Equal([4_120], pool.Requests);
        reservation.Span[..4].Fill(7);
        buffer.Advance(4);
        buffer.Advance(0);
        Assert.Equal([7, 7, 7, 7], buffer.WrittenMemory.ToArray());
        Assert.Throws<InvalidDataException>(() => buffer.Advance(1));
        Assert.Throws<InvalidDataException>(() => buffer.GetMemory(4_117));
        Assert.Equal(4, buffer.WrittenMemory.Length);
        Assert.Single(pool.Requests);
        buffer.Reset();
        buffer.GetSpan(4)[0] = 42;
        buffer.Advance(1);
        Assert.Equal([42], buffer.WrittenMemory.ToArray());
        Assert.Single(pool.Requests);
    }

    [Fact]
    public void LargerPoolBucketsCannotIncreaseTheExposedReservation()
    {
        TrackingPool pool = new() { ExtraCapacity = 8192 };
        using WalRecordBuffer buffer = new(4, pool, reserveForEncoding: true);
        Assert.Equal(4_120, buffer.GetMemory(4096).Length);
        Assert.Throws<InvalidDataException>(() => buffer.GetSpan(4_121));
        Assert.Single(pool.Requests);
    }

    [Theory]
    [InlineData(1, 4_102)]
    [InlineData(1_000_000_000, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void ReservationArithmeticSaturatesWithoutAllocatingOversizedArrays(int limit, int expected)
    {
        OutOfMemoryException failure = new("simulated rental failure");
        TrackingPool pool = new() { FailAt = 1, Failure = failure };
        using WalRecordBuffer buffer = new(limit, pool, reserveForEncoding: true);
        Assert.Same(failure, Assert.Throws<OutOfMemoryException>(() => buffer.GetMemory(expected)));
        Assert.Equal([expected], pool.Requests);
        Assert.Empty(buffer.WrittenMemory.ToArray());
        Assert.Empty(pool.Returned);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void EncodingReservationsStillRejectNegativeHintsAndAdvances(int value)
    {
        using WalRecordBuffer buffer = new(4, reserveForEncoding: true);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetMemory(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetSpan(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Advance(value));
    }

    [Fact]
    public void FailedGrowthKeepsEarlierOutputAndReturnsOnlyItsOwnedLoan()
    {
        TrackingPool pool = new() { FailAt = 2 };
        WalRecordBuffer buffer = new(32, pool, reserveForEncoding: true);
        buffer.GetSpan(16)[..12].Fill(8);
        buffer.Advance(12);
        Assert.Throws<OutOfMemoryException>(() => buffer.GetMemory(500));
        Assert.Equal(Enumerable.Repeat((byte)8, 12), buffer.WrittenMemory.ToArray());
        Assert.Equal(1, pool.Outstanding);
        buffer.Dispose();
        buffer.Dispose();
        Assert.Equal(0, pool.Outstanding);
        Assert.Single(pool.Returned);
        Assert.All(pool.Returned[0], value => Assert.Equal(0, value));
    }

    private static GlobalTelemetryPayload Event(string value) => new()
    {
        ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e",
        MetadataJson = JsonSerializer.Serialize(value, TelemetryAotContext.Default.String),
        Timestamp = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc)
    };

    private static byte[] Frame(GlobalTelemetryPayload payload) =>
        [.. JsonSerializer.SerializeToUtf8Bytes(payload, TelemetryAotContext.Default.GlobalTelemetryPayload), (byte)'\n'];

    private sealed class TrackingPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _owned = [];
        public int ExtraCapacity { get; init; }
        public int FailAt { get; init; }
        public OutOfMemoryException Failure { get; init; } = new("simulated rental failure");
        public int Outstanding => _owned.Count;
        public List<int> Requests { get; } = [];
        public List<byte[]> Returned { get; } = [];

        public override byte[] Rent(int minimumLength)
        {
            Requests.Add(minimumLength);
            if (Requests.Count == FailAt)
            {
                throw Failure;
            }
            byte[] bytes = new byte[minimumLength + ExtraCapacity];
            Assert.True(_owned.Add(bytes));
            return bytes;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(clearArray);
            Assert.True(_owned.Remove(array));
            Array.Clear(array);
            Returned.Add(array);
        }
    }
}

using System.Buffers;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalRecordBufferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BufferRequiresAPositiveLimit(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WalRecordBuffer(limit));
    }

    [Fact]
    public void GrowthKeepsPreviouslyWrittenBytesAndResetReusesTheBuffer()
    {
        using WalRecordBuffer buffer = new(100);
        buffer.GetSpan(16).Fill(7);
        buffer.Advance(16);
        buffer.GetMemory(50).Span[..50].Fill(9);
        buffer.Advance(50);
        Assert.Equal(Enumerable.Repeat((byte)7, 16).Concat(Enumerable.Repeat((byte)9, 50)), buffer.WrittenMemory.ToArray());

        buffer.Reset();
        Assert.Empty(buffer.WrittenMemory.ToArray());
        buffer.GetSpan()[0] = 42;
        buffer.Advance(1);
        Assert.Equal([42], buffer.WrittenMemory.ToArray());
    }

    [Fact]
    public void ReturnedMemoryCannotExposeCapacityPastTheLimit()
    {
        using WalRecordBuffer buffer = new(17);
        Memory<byte> memory = buffer.GetMemory(17);
        Assert.Equal(17, memory.Length);
        memory.Span.Fill(1);
        buffer.Advance(17);
        buffer.Advance(0);
        Assert.Equal(17, buffer.WrittenMemory.Length);
        Assert.Throws<InvalidDataException>(() => buffer.GetMemory());
        Assert.Throws<InvalidDataException>(() => buffer.GetSpan(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Advance(1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NegativeHintsAndAdvancesAreRejected(int value)
    {
        using WalRecordBuffer buffer = new(100);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetMemory(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetSpan(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Advance(value));
    }

    [Fact]
    public void OversizedRequestsAndAdvancesDoNotChangeWrittenData()
    {
        using WalRecordBuffer buffer = new(20);
        buffer.GetSpan(10)[0] = 5;
        buffer.Advance(1);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => buffer.GetMemory(20));
        Assert.Contains("WalMaxRecordBytes", error.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Advance(21));
        Assert.Equal([5], buffer.WrittenMemory.ToArray());
    }

    [Fact]
    public void EmptyAndRepeatedDisposalReturnToAnEmptyState()
    {
        WalRecordBuffer empty = new(16);
        empty.Advance(0);
        empty.Dispose();
        empty.Dispose();
        Assert.Empty(empty.WrittenMemory.ToArray());

        WalRecordBuffer written = new(16);
        written.GetSpan(1)[0] = 7;
        written.Advance(1);
        written.Dispose();
        written.Dispose();
        Assert.Empty(written.WrittenMemory.ToArray());
    }

    [Fact]
    public void PartialCapacityCannotBeMistakenForAvailableWritableSpace()
    {
        ExactPool pool = new();
        using WalRecordBuffer buffer = new(100, pool);
        buffer.GetSpan(16).Fill(7);
        buffer.Advance(12);
        Memory<byte> next = buffer.GetMemory(8);
        Assert.True(next.Length >= 8);
        next.Span[..8].Fill(9);
        buffer.Advance(8);
        Assert.Equal(Enumerable.Repeat((byte)7, 12).Concat(Enumerable.Repeat((byte)9, 8)), buffer.WrittenMemory.ToArray());
        Assert.Equal(2, pool.RentRequests.Count);
    }

    [Fact]
    public void ExactFitReusesItsExistingLoanAndGrowthNeverRequestsMoreThanTheLimit()
    {
        ExactPool pool = new();
        using WalRecordBuffer buffer = new(20, pool);
        buffer.GetMemory(16);
        buffer.Advance(12);
        Assert.Equal(4, buffer.GetMemory(4).Length);
        Assert.Single(pool.RentRequests);
        buffer.GetMemory(5);
        Assert.All(pool.RentRequests, requested => Assert.InRange(requested, 1, 20));
    }

    [Fact]
    public void SmallWritesHaveBoundedRentalOverheadAndReturnEveryClearedLoan()
    {
        ExactPool pool = new();
        WalRecordBuffer buffer = new(1024, pool);
        for (int index = 0; index < 512; index++)
        {
            buffer.GetSpan(1)[0] = 7;
            buffer.Advance(1);
        }
        Assert.InRange(pool.RentRequests.Count, 1, 10);
        Assert.Equal(Enumerable.Repeat((byte)7, 512), buffer.WrittenMemory.ToArray());
        buffer.Dispose();
        Assert.Equal(pool.RentRequests.Count, pool.Returns.Count);
        Assert.All(pool.Returns, returned => Assert.All(returned, value => Assert.Equal(0, value)));
        buffer.Dispose();
        Assert.Equal(pool.RentRequests.Count, pool.Returns.Count);
    }

    private sealed class ExactPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _owned = [];
        public List<int> RentRequests { get; } = [];
        public List<byte[]> Returns { get; } = [];

        public override byte[] Rent(int minimumLength)
        {
            RentRequests.Add(minimumLength);
            byte[] bytes = new byte[minimumLength];
            Assert.True(_owned.Add(bytes));
            return bytes;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(_owned.Remove(array));
            Assert.True(clearArray);
            Array.Clear(array);
            Returns.Add(array);
        }
    }
}

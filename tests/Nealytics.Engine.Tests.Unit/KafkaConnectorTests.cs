using System.Text.Json;
using Confluent.Kafka;
using Nealytics.Engine.Infrastructure.Serialization;
using NSubstitute;
using NSubstitute.Extensions;
using static Nealytics.Engine.Tests.Unit.KafkaTestSupport;

namespace Nealytics.Engine.Tests.Unit;

public sealed class KafkaConnectorTests
{
    [Fact]
    public async Task ConsumerCreationFailureIsSanitized()
    {
        await using var harness = new Harness(factory: () => throw new IOException("secret library detail"));
        await harness.Start();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Connector.ExecuteTask!);
        Assert.Contains("during create", error.Message);
        Assert.Contains("partition -1, offset -1", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Empty(harness.Consumer.ReceivedCalls());
    }

    [Fact]
    public async Task CancellationAfterCommitExitsWithoutConsumingAnotherRecord()
    {
        await using var harness = new Harness();
        harness.Consumer.Configure().Consume(Arg.Any<CancellationToken>()).Returns(Record());
        harness.Consumer.When(c => c.Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>())).Do(_ => harness.Stopping.Cancel());
        await harness.Start();
        await harness.Connector.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        harness.Consumer.Received(1).Consume(Arg.Any<CancellationToken>());
        harness.Consumer.Received(1).Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());
        harness.Consumer.Received(1).Close();
        harness.Consumer.Received(1).Dispose();
    }

    [Fact]
    public async Task WorkerStopWithoutApplicationShutdownIsNormalCancellation()
    {
        await using var harness = new Harness();
        await harness.Start();
        await WaitFor(() => harness.Consumer.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "Consume"));
        await harness.Connector.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Connector.ExecuteTask!;
        Assert.False(harness.Stopping.IsCancellationRequested);
        harness.Consumer.DidNotReceive().Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());
        harness.Consumer.Received(1).Close();
        harness.Consumer.Received(1).Dispose();
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("custom")]
    public async Task CommitsOnlyTheProcessedPartitionsNextOffsetAfterDurableHandoff(string path)
    {
        await using var harness = new Harness(path);
        int received = 0;
        harness.Consumer.Configure().Consume(Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (Interlocked.Increment(ref received) <= 2) return Record(offset: received * 10);
            var token = call.Arg<CancellationToken>();
            token.WaitHandle.WaitOne();
            throw new OperationCanceledException(token);
        });
        List<long> committed = [];
        harness.Consumer.When(c => c.Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>())).Do(call =>
        {
            var offset = Assert.Single(call.Arg<IEnumerable<TopicPartitionOffset>>());
            Assert.Equal("events", offset.Topic);
            Assert.Equal(2, offset.Partition.Value);
            Assert.True(harness.Broker.Reader.TryRead(out var delivered));
            Assert.Equal(Guid.Parse(Identity), delivered.EventId);
            Assert.Equal(committed.Count + 1, harness.Wal.UncommittedRecordCount);
            var lines = File.ReadAllLines(harness.WalPath);
            var persisted = JsonSerializer.Deserialize(lines[committed.Count], TelemetryAotContext.Default.GlobalTelemetryPayload)!;
            Assert.Equal(delivered.WalRecordId, persisted.WalRecordId);
            Assert.NotEqual(Guid.Empty, persisted.WalRecordId);
            committed.Add(offset.Offset.Value);
        });
        await harness.Start();
        await WaitFor(() => Volatile.Read(ref received) == 3);
        await harness.Stop();
        await harness.Connector.ExecuteTask!;
        Assert.Equal(new long[] { 11, 21 }, committed);
        harness.Consumer.Received(1).Subscribe("events");
        harness.Consumer.Received(1).Close();
        harness.Consumer.Received(1).Dispose();
        harness.Consumer.DidNotReceive().StoreOffset(Arg.Any<ConsumeResult<Ignore, byte[]>>());
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("custom")]
    public async Task CancellationWithFullBrokerPreservesWalAndDoesNotCommit(string path)
    {
        await using var harness = new Harness(path, capacity: 1);
        await harness.Broker.PublishAsync(new() { ProjectId = "p" }, CancellationToken.None);
        harness.Consumer.Configure().Consume(Arg.Any<CancellationToken>()).Returns(Record());
        await harness.Start();
        await WaitFor(() => harness.Wal.UncommittedRecordCount == 1);
        await harness.Stop();
        await harness.Connector.ExecuteTask!;
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        Assert.Equal(Guid.Parse(Identity), JsonSerializer.Deserialize(File.ReadAllLines(harness.WalPath).Single(), TelemetryAotContext.Default.GlobalTelemetryPayload)!.EventId);
        harness.Consumer.DidNotReceive().Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());
        harness.Consumer.Received(1).Close();
        harness.Consumer.Received(1).Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WriteAndFlushFailuresNeverCommitOrPublish(bool writeFailure)
    {
        await using var harness = new Harness(file: new FaultFile(writeFailure));
        harness.Consumer.Configure().Consume(Arg.Any<CancellationToken>()).Returns(Record());
        await harness.Start();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Connector.ExecuteTask!);
        Assert.Contains("persist-and-dispatch", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        Assert.False(harness.Broker.Reader.TryRead(out _));
        harness.Consumer.DidNotReceive().Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());
        harness.Consumer.Received(1).Close();
        harness.Consumer.Received(1).Dispose();
    }

    [Fact]
    public async Task CommitFailureStopsBeforeNextRecordAndLeavesDurableOwnership()
    {
        await using var harness = new Harness();
        harness.Consumer.Configure().Consume(Arg.Any<CancellationToken>()).Returns(Record());
        harness.Consumer.When(c => c.Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>())).Do(_ => throw new KafkaException(new Error(ErrorCode.Local_State, "secret broker detail")));
        await harness.Start();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Connector.ExecuteTask!);
        Assert.Contains("during commit", error.Message);
        Assert.Contains("partition 2, offset 41", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        Assert.True(harness.Broker.Reader.TryRead(out _));
        harness.Consumer.Received(1).Consume(Arg.Any<CancellationToken>());
        harness.Consumer.Received(1).Close();
        harness.Consumer.Received(1).Dispose();
    }

    [Fact]
    public async Task PoisonRecordStopsWithoutAdvancingOrEchoingInput()
    {
        await using var harness = new Harness();
        harness.Consumer.Configure().Consume(Arg.Any<CancellationToken>()).Returns(Record("secret input"));
        await harness.Start();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Connector.ExecuteTask!);
        Assert.Contains("during validate", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        harness.Consumer.Received(1).Consume(Arg.Any<CancellationToken>());
        harness.Consumer.DidNotReceive().Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());
        harness.Consumer.Received(1).Close();
        harness.Consumer.Received(1).Dispose();
    }

    [Theory]
    [InlineData("subscribe")]
    [InlineData("consume")]
    [InlineData("close")]
    [InlineData("dispose")]
    public async Task ConsumerFailuresAreSanitizedAndOwnershipIsReleased(string stage)
    {
        await using var harness = new Harness();
        if (stage == "subscribe") harness.Consumer.When(c => c.Subscribe("events")).Do(_ => throw new IOException("secret"));
        if (stage == "consume") harness.Consumer.Configure().Consume(Arg.Any<CancellationToken>()).Returns(_ => throw new OperationCanceledException("secret unrelated cancellation"));
        if (stage == "close") harness.Consumer.When(c => c.Close()).Do(_ => throw new IOException("secret"));
        if (stage == "dispose") harness.Consumer.When(c => c.Dispose()).Do(_ => throw new IOException("secret"));
        await harness.Start();
        if (stage is "close" or "dispose")
        {
            await WaitFor(() => harness.Consumer.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "Consume"));
            await harness.Stop();
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Connector.ExecuteTask!);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
        harness.Consumer.DidNotReceive().Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());
        harness.Consumer.Received(1).Close();
        harness.Consumer.Received(1).Dispose();
    }
}

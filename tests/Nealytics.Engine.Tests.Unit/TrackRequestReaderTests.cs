using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed class TrackRequestReaderTests
{
    [Theory]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    [InlineData("malformed", true)]
    public async Task CompletionIsOwnedOnSuccessfulAndMalformedInput(string json, bool malformed)
    {
        RecordingReader reader = new(PipeReader.Create(new MemoryStream(Encoding.UTF8.GetBytes(json))));
        if (malformed)
        {
            await Assert.ThrowsAsync<JsonException>(() => TrackRequestReader.ReadAsync(reader, CancellationToken.None));
        }
        else
        {
            GlobalTelemetryPayload? payload = await TrackRequestReader.ReadAsync(reader, CancellationToken.None);
            Assert.Equal(json == "null", payload is null);
        }
        Assert.Equal(1, reader.Completions);
    }

    [Fact]
    public async Task RetainedFragmentsAreExaminedBeforeWaitingAndCompletedOnce()
    {
        Pipe pipe = new();
        RecordingReader reader = new(pipe.Reader);
        await pipe.Writer.WriteAsync("{\"projectId\":"u8.ToArray());
        Task<GlobalTelemetryPayload?> reading = TrackRequestReader.ReadAsync(reader, CancellationToken.None);
        await reader.Advanced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(reading.IsCompleted);
        Assert.Equal(0, reader.ConsumedBytes);
        Assert.Equal(13, reader.ExaminedBytes);
        await pipe.Writer.WriteAsync("\"p\"}"u8.ToArray());
        await pipe.Writer.CompleteAsync();
        Assert.Equal("p", (await reading.WaitAsync(TimeSpan.FromSeconds(5)))!.ProjectId);
        Assert.Equal(1, reader.Completions);
    }

    [Fact]
    public async Task ReadCancellationCompletesTheOwnedReader()
    {
        Pipe pipe = new();
        RecordingReader reader = new(pipe.Reader);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TrackRequestReader.ReadAsync(reader, canceled.Token));
        Assert.Equal(1, reader.Completions);
        await pipe.Writer.CompleteAsync();
    }

    private sealed class RecordingReader(PipeReader inner) : PipeReader
    {
        private ReadOnlySequence<byte> _buffer;
        public int Completions { get; private set; }
        public long ConsumedBytes { get; private set; }
        public long ExaminedBytes { get; private set; }
        public TaskCompletionSource Advanced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadResult result = await inner.ReadAsync(cancellationToken);
            _buffer = result.Buffer;
            return result;
        }

        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            ConsumedBytes = _buffer.Slice(0, consumed).Length;
            ExaminedBytes = _buffer.Slice(0, examined).Length;
            inner.AdvanceTo(consumed, examined);
            Advanced.TrySetResult();
        }

        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null)
        {
            Completions++;
            inner.Complete(exception);
        }
    }
}

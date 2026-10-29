using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class BoundedRequestPipeReaderTests
{
    [Fact]
    public void ManagedExecutionRemainsTheDefault()
    {
        Assert.Equal("managed", new TelemetryEngineOptions().IngestionExecutionPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExaminedAndPartiallyConsumedBytesAreCountedOnce(bool synchronous)
    {
        Pipe pipe = new();
        BoundedRequestPipeReader reader = new(pipe.Reader, 5);
        await pipe.Writer.WriteAsync("abc"u8.ToArray());
        ReadResult first = await ReadAsync(reader, synchronous);
        Assert.Equal("abc", Encoding.UTF8.GetString(first.Buffer));
        reader.AdvanceTo(first.Buffer.GetPosition(1), first.Buffer.End);
        await pipe.Writer.WriteAsync("de"u8.ToArray());
        ReadResult second = await ReadAsync(reader, synchronous);
        Assert.Equal("bcde", Encoding.UTF8.GetString(second.Buffer));
        reader.AdvanceTo(second.Buffer.Start);
        ReadResult repeated = await ReadAsync(reader, synchronous);
        Assert.Equal("bcde", Encoding.UTF8.GetString(repeated.Buffer));
        reader.AdvanceTo(repeated.Buffer.End);
        await pipe.Writer.CompleteAsync();
        ReadResult final = await ReadAsync(reader, synchronous);
        Assert.True(final.IsCompleted);
        Assert.True(final.Buffer.IsEmpty);
        await reader.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsumedFragmentsCannotResetTheCumulativeByteBudget(bool synchronous)
    {
        Pipe pipe = new();
        BoundedRequestPipeReader reader = new(pipe.Reader, 3);
        await pipe.Writer.WriteAsync("ab"u8.ToArray());
        ReadResult first = await ReadAsync(reader, synchronous);
        reader.AdvanceTo(first.Buffer.End);
        await pipe.Writer.WriteAsync("cd"u8.ToArray());
        BadHttpRequestException error = await Assert.ThrowsAsync<BadHttpRequestException>(
            () => ReadAsync(reader, synchronous));
        Assert.Equal(413, error.StatusCode);
        Assert.Contains("3 bytes", error.Message);
        await reader.CompleteAsync();
        await pipe.Writer.WriteAsync("e"u8.ToArray());
        await pipe.Writer.CompleteAsync();
        ReadResult remaining = await pipe.Reader.ReadAsync();
        Assert.Equal("e", Encoding.UTF8.GetString(remaining.Buffer));
        pipe.Reader.AdvanceTo(remaining.Buffer.End);
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task EmptyPollAndCancellationDoNotCompleteTheBorrowedReader()
    {
        Pipe pipe = new();
        BoundedRequestPipeReader reader = new(pipe.Reader, 1);
        Assert.False(reader.TryRead(out _));
        ValueTask<ReadResult> pending = reader.ReadAsync();
        reader.CancelPendingRead();
        ReadResult canceled = await pending;
        Assert.True(canceled.IsCanceled);
        reader.AdvanceTo(canceled.Buffer.End);
        await reader.CompleteAsync();
        reader.Complete(new IOException("ignored by the borrowed reader"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadAsync().AsTask());
        Assert.Throws<ObjectDisposedException>(() => reader.TryRead(out _));
        Assert.Throws<ObjectDisposedException>(() => reader.AdvanceTo(default));
        await pipe.Writer.WriteAsync("x"u8.ToArray());
        await pipe.Writer.CompleteAsync();
        ReadResult remaining = await pipe.Reader.ReadAsync();
        Assert.Equal("x", Encoding.UTF8.GetString(remaining.Buffer));
        pipe.Reader.AdvanceTo(remaining.Buffer.End);
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutOwningTheServerPipe()
    {
        Pipe pipe = new();
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        BoundedRequestPipeReader reader = new(pipe.Reader, 4);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(canceled.Token).AsTask());
        await reader.CompleteAsync();
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Theory]
    [InlineData("managed", false)]
    [InlineData("custom", true)]
    [InlineData("CUSTOM", true)]
    public async Task ExecutionSelectionUsesTheConfiguredReaderAndKeepsTheExistingStream(string path, bool custom)
    {
        IngestionRequestReader selection = new(Options.Create(new TelemetryEngineOptions
        {
            IngestionExecutionPath = path,
            MaxRequestBodyBytes = 1
        }));
        Assert.Equal(custom, selection.UsesCustomPath);
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream("x"u8.ToArray());
        PipeReader reader = selection.Open(context);
        Assert.Equal(custom, reader is BoundedRequestPipeReader);
        ReadResult result = await reader.ReadAsync();
        Assert.Equal("x", Encoding.UTF8.GetString(result.Buffer));
        reader.AdvanceTo(result.Buffer.End);
        await reader.CompleteAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void InvalidExecutionSelectionFailsBeforeReadingInput(string? path)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => new IngestionRequestReader(Options.Create(new TelemetryEngineOptions
        {
            IngestionExecutionPath = path!
        })));
        Assert.Equal("options", error.ParamName);
        Assert.Contains("managed or custom", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidByteLimitsAreRejected(long limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedRequestPipeReader(new Pipe().Reader, limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestionRequestReader(Options.Create(new TelemetryEngineOptions
        {
            MaxRequestBodyBytes = (int)limit
        })));
    }

    [Fact]
    public void BorrowedReaderIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => new BoundedRequestPipeReader(null!, 1));
    }

    private static Task<ReadResult> ReadAsync(PipeReader reader, bool synchronous)
    {
        if (synchronous)
        {
            Assert.True(reader.TryRead(out ReadResult result));
            return Task.FromResult(result);
        }
        return reader.ReadAsync().AsTask();
    }
}

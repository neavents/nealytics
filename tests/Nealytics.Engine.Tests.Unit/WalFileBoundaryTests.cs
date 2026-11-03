using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalFileBoundaryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-file-boundary-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AcknowledgmentWaitsForTheFileDurabilityBarrier()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestWalFile file = new()
        {
            Flush = async () =>
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        await using WriteAheadLogger wal = new(Options(), file);
        GlobalTelemetryPayload payload = Event();
        Task append = wal.AppendAsync(payload, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(append.IsCompleted);
        Assert.Equal(0, wal.UncommittedRecordCount);
        byte[] bytes = Assert.Single(file.Records);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.Equal(payload.EventId, JsonSerializer.Deserialize(
            bytes, TelemetryAotContext.Default.GlobalTelemetryPayload)!.EventId);

        release.SetResult();
        await append.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, wal.UncommittedRecordCount);
        Assert.Equal(1, wal.FlushedRecordCount);
        Assert.Equal(1, wal.FlushGroupCount);
        Assert.Equal(1, wal.AverageGroupSize);
        Assert.True(wal.AverageFlushMilliseconds > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FileWriteOrFlushFailureDoesNotAcknowledgeAnAppend(bool writeFails)
    {
        IOException failure = new("injected storage failure");
        TestWalFile file = new();
        if (writeFails)
        {
            file.Append = _ => ValueTask.FromException(failure);
        }
        else
        {
            file.Flush = () => ValueTask.FromException(failure);
        }
        await using WriteAheadLogger wal = new(Options(), file);

        IOException actual = await Assert.ThrowsAsync<IOException>(
            () => wal.AppendAsync(Event(), CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Equal(0, wal.UncommittedRecordCount);
        Assert.Equal(0, wal.FlushedRecordCount);
        Assert.Equal(0, wal.FlushGroupCount);
        Assert.Equal(0, wal.AverageGroupSize);
        Assert.Equal(0, wal.AverageFlushMilliseconds);
        Assert.Equal(writeFails ? 0 : 1, file.FlushCalls);
    }

    [Fact]
    public async Task ACheckpointWaitingBehindAFailedFlushCannotDeleteDeliveryMetadata()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IOException failure = new("flush failed before checkpoint");
        TestWalFile file = new()
        {
            Flush = async () =>
            {
                entered.TrySetResult();
                await release.Task;
                throw failure;
            }
        };
        await using WriteAheadLogger wal = new(Options(), file);
        Task append = wal.AppendAsync(Event(), CancellationToken.None);
        Task? checkpoint = null;
        bool cleared = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            checkpoint = wal.CheckpointDeliveryAsync(() => cleared = true);
            Assert.False(checkpoint.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => append.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => checkpoint!.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.False(cleared);
        Assert.Equal(0, wal.UncommittedRecordCount);
        Assert.Equal(0, file.TruncateCalls);
    }

    [Fact]
    public async Task PartialCommitKeepsTheFileAndFinalCommitTruncatesIt()
    {
        TestWalFile file = new();
        await using WriteAheadLogger wal = new(Options(), file);
        await wal.AppendAsync(Event(), CancellationToken.None);
        await wal.AppendAsync(Event(), CancellationToken.None);

        await wal.AcknowledgeCommitAsync(1);
        Assert.Equal(0, file.TruncateCalls);
        Assert.Equal(1, wal.UncommittedRecordCount);

        await wal.AcknowledgeCommitAsync(1);
        Assert.Equal(1, file.TruncateCalls);
        Assert.Equal(0, wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task ExplicitTruncationUsesTheFileBoundary()
    {
        TestWalFile file = new();
        await using WriteAheadLogger wal = new(Options(), file);
        await wal.AppendAsync(Event(), CancellationToken.None);

        await wal.TruncateAsync();

        Assert.Equal(1, file.TruncateCalls);
        Assert.Equal(0, wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TruncationFailureStopsFurtherStorageOperationsAndPreservesTheRecordCount(bool acknowledge)
    {
        IOException failure = new("truncate failure");
        TestWalFile file = new() { Truncate = () => ValueTask.FromException(failure) };
        await using WriteAheadLogger wal = new(Options(), file);
        await wal.AppendAsync(Event(), CancellationToken.None);

        IOException actual = await Assert.ThrowsAsync<IOException>(
            () => acknowledge ? wal.AcknowledgeCommitAsync(1) : wal.TruncateAsync());

        Assert.Same(failure, actual);
        Assert.Equal(1, wal.UncommittedRecordCount);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => wal.TruncateAsync()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => wal.AcknowledgeCommitAsync(1)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => wal.AppendAsync(Event(), CancellationToken.None)));
        Assert.Equal(1, file.TruncateCalls);
    }

    [Fact]
    public async Task DisposalDrainsAdmittedWritesBeforeDisposingTheFile()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestWalFile file = new()
        {
            Flush = async () =>
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        WriteAheadLogger wal = new(Options(), file);
        Task append = wal.AppendAsync(Event(), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposal = wal.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.False(file.Disposed);

        release.SetResult();
        await Task.WhenAll(append, disposal).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(file.Disposed);
        Assert.Equal(1, wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task ManagedFilePersistsBytesAndCanAppendAfterTruncationWithoutGaps()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "managed.log");
        await File.WriteAllTextAsync(path, "existing\n");
        byte[] unicode = Encoding.UTF8.GetBytes("value-λ\n");
        await using (ManagedWalFile file = new(path, 8))
        {
            await file.AppendAsync(unicode);
            await file.FlushAsync();
            Assert.Equal("existing\nvalue-λ\n", await File.ReadAllTextAsync(path));
            await file.TruncateAsync();
            Assert.Empty(await File.ReadAllBytesAsync(path));
            await file.AppendAsync("new\n"u8.ToArray());
            await file.FlushAsync();
            Assert.Equal("new\n", await File.ReadAllTextAsync(path));
        }

        using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(4, exclusive.Length);
    }

    [Fact]
    public async Task ManagedFileRejectsOperationsAfterDisposal()
    {
        Directory.CreateDirectory(_directory);
        ManagedWalFile file = new(Path.Combine(_directory, "closed.log"), 4096);
        await file.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => file.AppendAsync("x"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => file.FlushAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => file.TruncateAsync().AsTask());
        await file.DisposeAsync();
    }

    [Fact]
    public async Task ManagedDurabilityBarrierWaitsForBufferedWritesAndRequestsADiskFlush()
    {
        Directory.CreateDirectory(_directory);
        ProbeFileStream stream = new(Path.Combine(_directory, "barrier.log"));
        await using ManagedWalFile file = new(stream);
        Task flush = file.FlushAsync().AsTask();
        await stream.FlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(flush.IsCompleted);
        Assert.Empty(stream.DiskFlushes);
        stream.ReleaseFlush.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([true], stream.DiskFlushes);

        await file.TruncateAsync();
        Assert.Equal([true, true], stream.DiskFlushes);
    }

    private IOptions<TelemetryEngineOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new TelemetryEngineOptions { WriteAheadLogDirectory = _directory });

    private static GlobalTelemetryPayload Event() => new()
    {
        ProjectId = "p",
        TenantId = "t",
        SessionId = "s",
        EventType = "e"
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private sealed class TestWalFile : IWalFile
    {
        public Func<ReadOnlyMemory<byte>, ValueTask>? Append { get; set; }
        public Func<ValueTask>? Flush { get; set; }
        public Func<ValueTask>? Truncate { get; set; }
        public List<byte[]> Records { get; } = [];
        public int FlushCalls { get; private set; }
        public int TruncateCalls { get; private set; }
        public bool Disposed { get; private set; }

        public async ValueTask AppendAsync(ReadOnlyMemory<byte> record)
        {
            Records.Add(record.ToArray());
            if (Append is not null)
            {
                await Append(record);
            }
        }

        public async ValueTask FlushAsync()
        {
            FlushCalls++;
            if (Flush is not null)
            {
                await Flush();
            }
        }

        public async ValueTask TruncateAsync()
        {
            TruncateCalls++;
            if (Truncate is not null)
            {
                await Truncate();
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProbeFileStream(string path) : FileStream(path, FileMode.Create)
    {
        public TaskCompletionSource FlushEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFlush { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<bool> DiskFlushes { get; } = [];

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushEntered.TrySetResult();
            await ReleaseFlush.Task.WaitAsync(cancellationToken);
        }

        public override void Flush(bool flushToDisk)
        {
            DiskFlushes.Add(flushToDisk);
            base.Flush(flushToDisk);
        }
    }
}

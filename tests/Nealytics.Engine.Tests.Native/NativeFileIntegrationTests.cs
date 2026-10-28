using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal.Native;

namespace Nealytics.Engine.Tests.Native;

public sealed class NativeFileIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-native-file-" + Guid.NewGuid().ToString("N"));

    public NativeFileIntegrationTests()
    {
        Assert.True(OperatingSystem.IsLinux(), "This qualification project requires a real Linux host.");
        Assert.Contains(RuntimeInformation.ProcessArchitecture, new[] { Architecture.X64, Architecture.Arm64 });
        Assert.Equal(1U, NativeWalMethods.AbiVersion());
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task LinkedWriteAndFsyncPersistTheCopiedBytes()
    {
        string path = Path.Combine(_directory, "records.log");
        await using NativeWalFile file = new(new LiburingWalContext(path, 4096, 8));
        byte[] bytes = Encoding.UTF8.GetBytes("native-λ\n");
        await file.AppendAsync(bytes);
        bytes.AsSpan().Fill(0);
        Assert.Empty(await File.ReadAllBytesAsync(path));

        await file.FlushAsync();

        Assert.Equal("native-λ\n", await File.ReadAllTextAsync(path));
        await file.AppendAsync("next\n"u8.ToArray());
        await file.FlushAsync();
        Assert.Equal("native-λ\nnext\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExistingBytesSurviveAndTruncationResetsTheWriteOffset()
    {
        string path = Path.Combine(_directory, "offset.log");
        await File.WriteAllTextAsync(path, "old\n");
        await using NativeWalFile file = new(new LiburingWalContext(path, 4096, 8));
        await file.AppendAsync("second\n"u8.ToArray());
        await file.FlushAsync();
        Assert.Equal("old\nsecond\n", await File.ReadAllTextAsync(path));

        await file.TruncateAsync();
        Assert.Empty(await File.ReadAllBytesAsync(path));
        await file.AppendAsync("new\n"u8.ToArray());
        await file.FlushAsync();
        Assert.Equal("new\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EmptyAppendsAndRepeatedEmptyBarriersAreValid()
    {
        string path = Path.Combine(_directory, "empty.log");
        await using NativeWalFile file = new(new LiburingWalContext(path, 4096, 8));
        await file.AppendAsync(ReadOnlyMemory<byte>.Empty);
        await file.FlushAsync();
        await file.FlushAsync();
        await file.TruncateAsync();
        Assert.Empty(await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task NativeFilesUseTheSharedWalAccountingAndRemainReadableByManagedRecovery()
    {
        TelemetryEngineOptions settings = new()
        {
            WriteAheadLogDirectory = _directory,
            WalMaxPendingRecords = 512,
            WalMaxPendingBytes = 65_536,
            WalMaxRecordBytes = 16_384,
            WalMaxFlushRecords = 32,
            WalMaxFlushBytes = 16_384
        };
        string path = Path.Combine(_directory, "telemetry_wal.log");
        NativeWalFile native = new(new LiburingWalContext(path, settings.WalMaxFlushBytes, 8));
        IOptions<TelemetryEngineOptions> options = Options.Create(settings);
        Guid[] expected = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToArray();
        await using (WriteAheadLogger wal = new(options, native))
        {
            await Task.WhenAll(expected.Select(id => wal.AppendAsync(new GlobalTelemetryPayload
            {
                EventId = id,
                ProjectId = "p",
                TenantId = "t",
                SessionId = "s",
                EventType = "e"
            }, CancellationToken.None)));
            Assert.Equal(200, wal.UncommittedRecordCount);
            Assert.Equal(200, wal.FlushedRecordCount);
            Assert.Equal(0, wal.PendingRecordCount);
            Assert.Equal(0, wal.PendingByteCount);
        }

        await using WriteAheadLogger managed = new(options);
        IReadOnlyList<GlobalTelemetryPayload> recovered = await managed.ReplayUncommittedAsync();
        Assert.Equal(expected.Order(), recovered.Select(payload => payload.EventId).Order());
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(-1, 8)]
    [InlineData(4096, 7)]
    [InlineData(4096, 4097)]
    public void InvalidCapacitiesAreRefusedBeforeOpeningTheFile(int capacity, int entries)
    {
        string path = Path.Combine(_directory, "invalid.log");
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiburingWalContext(path, capacity, entries));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CreateFailuresCloseTheFileAndDoNotPreventItsRemoval()
    {
        string missing = Path.Combine(_directory, "missing", "file.log");
        Assert.Throws<DirectoryNotFoundException>(() => new LiburingWalContext(missing, 4096, 8));
        int status = NativeWalMethods.Create(999_999, 8, 4096, out nint context);
        Assert.Equal(-9, status);
        Assert.Equal(0, context);
    }

    [Fact]
    public void CapacityFailureDoesNotChangeBufferedRecords()
    {
        string path = Path.Combine(_directory, "capacity.log");
        using LiburingWalContext context = new(path, 8, 8);
        context.Append("first"u8.ToArray());
        IOException error = Assert.Throws<IOException>(() => context.Append(new byte[4]));
        Assert.Contains("errno 7", error.Message);
        context.Flush();
        Assert.Equal("first", File.ReadAllText(path));
    }

    [Fact]
    public void DirtyBuffersCannotBeSilentlyTruncated()
    {
        string path = Path.Combine(_directory, "dirty.log");
        using LiburingWalContext context = new(path, 4096, 8);
        context.Append("pending\n"u8.ToArray());
        IOException error = Assert.Throws<IOException>(() => context.Truncate());
        Assert.Contains("errno 16", error.Message);
        context.Flush();
        Assert.Equal("pending\n", File.ReadAllText(path));
    }

    [Fact]
    public void DisposedContextsRejectAllOperationsAndReleaseTheFile()
    {
        string path = Path.Combine(_directory, "disposed.log");
        LiburingWalContext context = new(path, 4096, 8);
        context.Dispose();
        context.Dispose();
        Assert.Throws<ObjectDisposedException>(() => context.Append(new byte[1]));
        Assert.Throws<ObjectDisposedException>(() => context.Flush());
        Assert.Throws<ObjectDisposedException>(() => context.Truncate());
        using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(0, exclusive.Length);
    }

    [Fact]
    public void ARealDiskWriteFailureIsReportedAndAllCompletionsAreDrained()
    {
        using LiburingWalContext context = new("/dev/full", 4096, 8);
        context.Append("record\n"u8.ToArray());
        IOException first = Assert.Throws<IOException>(() => context.Flush());
        Assert.Contains("errno 28", first.Message);
        IOException next = Assert.Throws<IOException>(() => context.Flush());
        Assert.Contains("errno 28", next.Message);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}

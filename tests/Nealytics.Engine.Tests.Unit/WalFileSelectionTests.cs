using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;
using Nealytics.Engine.Infrastructure.Storage.Wal.Native;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalFileSelectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-wal-selection-" + Guid.NewGuid().ToString("N"));

    private string PathName => Path.Combine(_directory, "telemetry_wal.log");

    public WalFileSelectionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task DefaultSelectionUsesManagedIoWithoutLoadingTheNativeBridge()
    {
        TelemetryEngineOptions options = Settings();
        Assert.Equal("managed", options.WalIoBackend);
        Assert.Equal(8, options.WalRingEntries);
        WalFileFactory factory = new(options, (_, _, _) => throw new Exception("native must stay unused"));
        WalFileSelection selection = factory.Create(PathName, new WalLimits(options));
        await using IWalFile file = selection.File;
        Assert.IsType<ManagedWalFile>(file);
        Assert.Equal("managed", selection.Backend);
        Assert.Equal(string.Empty, selection.FallbackReason);
        Assert.False(selection.RegisteredFile);
        await file.AppendAsync("durable\n"u8.ToArray());
        await file.FlushAsync();
        Assert.Equal("durable\n", await File.ReadAllTextAsync(PathName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("native")]
    [InlineData("AUTO")]
    public async Task InvalidModeDoesNotRenameOrChangeAnExistingWal(string? mode)
    {
        await File.WriteAllTextAsync(PathName, "unchanged\n");
        TelemetryEngineOptions options = Settings();
        options.WalIoBackend = mode!;
        ArgumentException error = Assert.Throws<ArgumentException>(() => new WriteAheadLogger(Options.Create(options)));
        Assert.Contains("managed, io_uring or auto", error.Message);
        Assert.Equal("unchanged\n", await File.ReadAllTextAsync(PathName));
        Assert.False(File.Exists(Path.Combine(_directory, "telemetry_wal.replay")));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(4097)]
    public void InvalidRingLimitsAreRefusedBeforeStorageSideEffects(int entries)
    {
        TelemetryEngineOptions options = Settings();
        options.WalRingEntries = entries;
        Assert.Throws<ArgumentOutOfRangeException>(() => new WalFileFactory(options));
        Assert.False(File.Exists(PathName));
    }

    [Theory]
    [InlineData("io_uring", false, 8)]
    [InlineData("io_uring", true, 4096)]
    [InlineData("auto", false, 4096)]
    [InlineData("auto", true, 8)]
    public async Task NativeSelectionPassesTheConfiguredBoundsAndReportsFileRegistration(string mode, bool registered, int entries)
    {
        TelemetryEngineOptions options = Settings(mode);
        options.WalRingEntries = entries;
        options.WalMaxFlushBytes = 32_768;
        options.WalMaxRecordBytes = 16_384;
        TestContext context = new() { Registered = registered };
        NativeWalFile native = new(context);
        int calls = 0;
        WalFileFactory factory = new(options, (path, capacity, ringEntries) =>
        {
            Assert.Equal(PathName, path);
            Assert.Equal(32_768, capacity);
            Assert.Equal(entries, ringEntries);
            calls++;
            return native;
        });
        WalFileSelection selection = factory.Create(PathName, new WalLimits(options));
        await using IWalFile file = selection.File;
        Assert.Same(native, file);
        Assert.Equal(1, calls);
        Assert.Equal("io_uring", selection.Backend);
        Assert.Equal(string.Empty, selection.FallbackReason);
        Assert.Equal(registered, selection.RegisteredFile);
        await file.AppendAsync("record\n"u8.ToArray());
        await file.FlushAsync();
        Assert.Equal("record\n", System.Text.Encoding.UTF8.GetString(Assert.Single(context.Records)));
        Assert.Equal(1, context.FlushCalls);
    }

    public static IEnumerable<object[]> CapabilityFailures()
    {
        yield return [new PlatformNotSupportedException("unsupported platform")];
        yield return [new DllNotFoundException("missing packaged bridge")];
        foreach (int code in new[] { 1, 13, 38, 95 })
        {
            yield return [new NativeWalException(code, "create")];
        }
        yield return [new NativeWalException(38, "flush")];
        yield return [new NativeWalException(95, "flush")];
    }

    public static IEnumerable<object[]> NonCapabilityFailures()
    {
        yield return [new IOException("storage failure")];
        yield return [new UnauthorizedAccessException("file permission failure")];
        yield return [new OutOfMemoryException("allocation failure")];
        yield return [new InvalidOperationException("ABI mismatch")];
        yield return [new EntryPointNotFoundException("wrong bridge")];
        yield return [new BadImageFormatException("wrong architecture")];
        foreach (int code in new[] { 5, 12, 22, 28 })
        {
            yield return [new NativeWalException(code, "create")];
        }
        foreach (int code in new[] { 1, 5, 12, 13, 28 })
        {
            yield return [new NativeWalException(code, "flush")];
        }
        yield return [new NativeWalException(95, "append")];
        yield return [new NativeWalException(95, "destroy")];
    }

    [Theory]
    [MemberData(nameof(CapabilityFailures))]
    public async Task AutoFallsBackOnlyDuringStartupForKnownCapabilityFailures(Exception error)
    {
        TelemetryEngineOptions options = Settings("auto");
        WalFileFactory factory = new(options, (_, _, _) => throw error);
        Assert.True(WalFileFactory.CanFallBack(error));
        WalFileSelection selection = factory.Create(PathName, new WalLimits(options));
        await using IWalFile file = selection.File;
        Assert.IsType<ManagedWalFile>(file);
        Assert.Equal("managed", selection.Backend);
        Assert.Equal(error.Message, selection.FallbackReason);
        Assert.False(selection.RegisteredFile);
        await file.AppendAsync("fallback\n"u8.ToArray());
        await file.FlushAsync();
        Assert.Equal("fallback\n", await File.ReadAllTextAsync(PathName));
    }

    [Theory]
    [MemberData(nameof(CapabilityFailures))]
    [MemberData(nameof(NonCapabilityFailures))]
    public void ExplicitNativeModePreservesEveryStartupFailure(Exception error)
    {
        TelemetryEngineOptions options = Settings("io_uring");
        WalFileFactory factory = new(options, (_, _, _) => throw error);
        Assert.Same(error, Record.Exception(() => factory.Create(PathName, new WalLimits(options))));
        Assert.False(File.Exists(PathName));
    }

    [Theory]
    [MemberData(nameof(NonCapabilityFailures))]
    public void AutoDoesNotConcealStorageAllocationAbiOrUnexpectedFailures(Exception error)
    {
        TelemetryEngineOptions options = Settings("auto");
        WalFileFactory factory = new(options, (_, _, _) => throw error);
        Assert.False(WalFileFactory.CanFallBack(error));
        Assert.Same(error, Record.Exception(() => factory.Create(PathName, new WalLimits(options))));
        Assert.False(File.Exists(PathName));
    }

    [Fact]
    public void FailureToOpenTheManagedFallbackIsNotHidden()
    {
        TelemetryEngineOptions options = Settings("auto");
        WalFileFactory factory = new(options, (_, _, _) => throw new PlatformNotSupportedException());
        Assert.Throws<DirectoryNotFoundException>(() => factory.Create(
            Path.Combine(_directory, "missing", "file.log"), new WalLimits(options)));
    }

    [Fact]
    public async Task NativePreflightTransfersOwnershipOnlyAfterItsBarrierSucceeds()
    {
        TestContext context = new() { Registered = true };
        NativeWalFile file = WalFileFactory.PrepareNative(context);
        Assert.Equal(1, context.FlushCalls);
        Assert.Equal(0, context.DisposeCalls);
        Assert.True(file.IsFileRegistered);
        await file.FlushAsync();
        Assert.Equal(2, context.FlushCalls);
        await file.DisposeAsync();
        Assert.Equal(1, context.DisposeCalls);
    }

    [Fact]
    public void FailedPreflightClosesTheNativeContextBeforeReturningAnError()
    {
        IOException failure = new("preflight failure");
        TestContext context = new() { FlushFailure = failure };
        Assert.Same(failure, Assert.Throws<IOException>(() => WalFileFactory.PrepareNative(context)));
        Assert.Equal(1, context.FlushCalls);
        Assert.Equal(1, context.DisposeCalls);
    }

    [Fact]
    public async Task InjectedFileHasExplicitDiagnosticMetadataAndRetainsItsOwnershipContract()
    {
        TestFile file = new();
        WriteAheadLogger wal = new(Options.Create(Settings()), file);
        Assert.Same(file, wal.Selection.File);
        Assert.Equal("injected", wal.Selection.Backend);
        Assert.Empty(wal.Selection.FallbackReason);
        Assert.False(wal.Selection.RegisteredFile);
        await wal.DisposeAsync();
        Assert.Equal(1, file.DisposeCalls);
    }

    [Fact]
    public async Task AutoNeverSwitchesBackendAfterAnAdmittedOperationFails()
    {
        TelemetryEngineOptions options = Settings("auto");
        IOException failure = new("runtime flush failure");
        TestContext context = new() { FlushFailure = failure };
        int calls = 0;
        WalFileFactory factory = new(options, (_, _, _) =>
        {
            calls++;
            return new NativeWalFile(context);
        });
        await using WriteAheadLogger wal = new(Options.Create(options), file: null, fileFactory: factory);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => wal.AppendAsync(Event(), CancellationToken.None)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => wal.AppendAsync(Event(), CancellationToken.None)));
        Assert.Equal("io_uring", wal.Selection.Backend);
        Assert.Equal(1, calls);
        Assert.Equal(1, context.FlushCalls);
        Assert.Equal(0, wal.UncommittedRecordCount);
        Assert.False(File.Exists(PathName));
    }

    [Fact]
    public async Task ExistingWalIsSealedBeforeTheNativeContextIsConstructed()
    {
        TelemetryEngineOptions options = Settings();
        GlobalTelemetryPayload first = Event();
        await using (WriteAheadLogger original = new(Options.Create(options)))
        {
            await original.AppendAsync(first, CancellationToken.None);
        }
        byte[] originalBytes = await File.ReadAllBytesAsync(PathName);
        options.WalIoBackend = "io_uring";
        TestContext context = new();
        int calls = 0;
        WalFileFactory factory = new(options, (_, _, _) =>
        {
            Assert.False(File.Exists(PathName));
            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(_directory, "telemetry_wal.replay")));
            calls++;
            return new NativeWalFile(context);
        });
        await using WriteAheadLogger wal = new(Options.Create(options), file: null, fileFactory: factory);
        Assert.Equal(1, calls);
        Assert.Equal("io_uring", wal.Selection.Backend);
        Assert.Equal(first.EventId, Assert.Single(await wal.ReplayUncommittedAsync()).EventId);
        await wal.AppendAsync(Event(), CancellationToken.None);
        Assert.Single(context.Records);
        Assert.Equal(1, context.FlushCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStartupReleasesTransferredResourcesAndRetainsCleanupErrors(bool cleanupFails)
    {
        string invalidDirectory = Path.Combine(_directory, "directory-is-a-file");
        await File.WriteAllTextAsync(invalidDirectory, "keep");
        TelemetryEngineOptions options = Settings();
        options.WriteAheadLogDirectory = invalidDirectory;
        TestFile file = new() { CleanupFails = cleanupFails };
        WalSynchronization synchronization = new();
        Exception? error = Record.Exception(() => new WriteAheadLogger(
            Options.Create(options), file, synchronization: synchronization));
        if (cleanupFails)
        {
            AggregateException aggregate = Assert.IsType<AggregateException>(error);
            Assert.Contains("startup and file cleanup", aggregate.Message);
            Assert.IsType<IOException>(aggregate.InnerExceptions[0]);
            Assert.Same(file.Failure, aggregate.InnerExceptions[1]);
        }
        else
        {
            Assert.IsType<IOException>(error);
        }
        Assert.Equal(1, file.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => synchronization.File.Wait(0));
        Assert.Throws<ObjectDisposedException>(() => synchronization.Serialization.Wait(0));
        Assert.Equal("keep", await File.ReadAllTextAsync(invalidDirectory));
    }

    [Fact]
    public void FailedNativeConstructionReleasesSynchronizationWithoutChangingTheReplaySegment()
    {
        TelemetryEngineOptions options = Settings("io_uring");
        IOException failure = new("startup failure");
        WalFileFactory factory = new(options, (_, _, _) => throw failure);
        WalSynchronization synchronization = new();
        Assert.Same(failure, Assert.Throws<IOException>(() => new WriteAheadLogger(
            Options.Create(options), file: null, synchronization: synchronization, fileFactory: factory)));
        Assert.Throws<ObjectDisposedException>(() => synchronization.File.Wait(0));
        Assert.Throws<ObjectDisposedException>(() => synchronization.Serialization.Wait(0));
    }

    [Fact]
    public async Task StartupDiagnosticsReportTheSelectedBackend()
    {
        TestLogger logger = new();
        await using WriteAheadLogger wal = new(Options.Create(Settings()), logger);
        Assert.Equal("managed", wal.Selection.Backend);
        Assert.Equal(LogLevel.Information, Assert.Single(logger.Levels));
        Assert.Equal(9010, Assert.Single(logger.Events).Id);
        string message = Assert.Single(logger.Messages);
        Assert.Contains("backend managed", message);
        Assert.Contains("registered file False", message);
        Assert.Contains("startup fallback", message);
    }

    [Fact]
    public void StartupDiagnosticsFailureClosesTheWalBeforePropagating()
    {
        TestLogger logger = new() { Failure = new InvalidOperationException("logger failed") };
        Assert.Same(logger.Failure, Assert.Throws<InvalidOperationException>(
            () => new WriteAheadLogger(Options.Create(Settings()), logger)));
        using FileStream exclusive = new(PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(0, exclusive.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void InvalidDirectoryIsRejectedBeforeTakingOwnershipOfResources(string? directory)
    {
        TelemetryEngineOptions options = Settings();
        options.WriteAheadLogDirectory = directory!;
        ArgumentException error = Assert.ThrowsAny<ArgumentException>(() => new WriteAheadLogger(Options.Create(options)));
        Assert.Equal("directory", error.ParamName);
    }

    private TelemetryEngineOptions Settings(string mode = "managed") => new()
    {
        WriteAheadLogDirectory = _directory,
        WalIoBackend = mode
    };

    private static GlobalTelemetryPayload Event() => new()
    {
        ProjectId = "p",
        TenantId = "t",
        SessionId = "s",
        EventType = "e"
    };

    public void Dispose() => Directory.Delete(_directory, true);

    private sealed class TestContext : INativeWalContext
    {
        public bool Registered { get; init; }
        public bool IsFileRegistered => Registered;
        public List<byte[]> Records { get; } = [];
        public int FlushCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public IOException? FlushFailure { get; init; }
        public void Append(ReadOnlyMemory<byte> record) => Records.Add(record.ToArray());
        public void Flush()
        {
            FlushCalls++;
            if (FlushFailure is not null)
            {
                throw FlushFailure;
            }
        }
        public void Truncate() { }
        public void Dispose() => DisposeCalls++;
    }

    private sealed class TestFile : IWalFile
    {
        public bool CleanupFails { get; init; }
        public IOException Failure { get; } = new("cleanup failure");
        public int DisposeCalls { get; private set; }
        public ValueTask AppendAsync(ReadOnlyMemory<byte> record) => ValueTask.CompletedTask;
        public ValueTask FlushAsync() => ValueTask.CompletedTask;
        public ValueTask TruncateAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return CleanupFails ? ValueTask.FromException(Failure) : ValueTask.CompletedTask;
        }
    }

    private sealed class TestLogger : ILogger<WriteAheadLogger>
    {
        public InvalidOperationException? Failure { get; init; }
        public List<LogLevel> Levels { get; } = [];
        public List<EventId> Events { get; } = [];
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Failure is not null)
            {
                throw Failure;
            }
            Levels.Add(logLevel);
            Events.Add(eventId);
            Messages.Add(formatter(state, exception));
        }
    }
}

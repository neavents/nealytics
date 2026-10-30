using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalDirectoryDurabilityTests
{
    [Fact]
    public void InterruptedDirectoryOpeningAndSynchronizationRetryWithoutLosingHandleOwnership()
    {
        using SafeFileHandle handle = FakeHandle();
        Operations operations = new();
        operations.Opens.Enqueue(new WalDirectoryOpen(null, 4));
        operations.Opens.Enqueue(new WalDirectoryOpen(handle, 4));
        operations.Synchronizations.Enqueue(new WalDirectoryStatus(-1, 4));
        operations.Synchronizations.Enqueue(new WalDirectoryStatus(0, 4));

        WalDirectoryDurability.Synchronize("directory", operations);

        Assert.True(handle.IsClosed);
        Assert.Equal(new[] { "open:directory", "open:directory", "sync", "sync" }, operations.Trace);
        Assert.Equal(operations.OpenFlags, operations.Flags);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(13)]
    public void OpeningFailureDoesNotRetryAnotherErrorOrAttemptSynchronization(int error)
    {
        Operations operations = new();
        operations.Opens.Enqueue(new WalDirectoryOpen(null, error));

        var failure = Assert.Throws<IOException>(() => WalDirectoryDurability.Synchronize("missing", operations));

        Assert.Equal("Could not synchronize WAL directory 'missing'.", failure.Message);
        Assert.Equal(error, Assert.IsType<Win32Exception>(failure.InnerException).NativeErrorCode);
        Assert.Equal(new[] { "open:missing" }, operations.Trace);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public void SynchronizationFailureClosesTheOpenedHandleAndPreservesItsCapturedError(int error)
    {
        using SafeFileHandle handle = FakeHandle();
        Operations operations = new();
        operations.Opens.Enqueue(new WalDirectoryOpen(handle, 0));
        operations.Synchronizations.Enqueue(new WalDirectoryStatus(-1, error));
        Marshal.SetLastPInvokeError(123);

        var failure = Assert.Throws<IOException>(() => WalDirectoryDurability.Synchronize("retained", operations));

        Assert.True(handle.IsClosed);
        Assert.Equal(error, Assert.IsType<Win32Exception>(failure.InnerException).NativeErrorCode);
        Assert.Equal(new[] { "open:retained", "sync" }, operations.Trace);
    }

    [Fact]
    public void AnExceptionDuringSynchronizationStillClosesItsOwnedHandle()
    {
        using SafeFileHandle handle = FakeHandle();
        IOException failure = new("synchronization failed");
        Operations operations = new() { SynchronizationFailure = failure };
        operations.Opens.Enqueue(new WalDirectoryOpen(handle, 0));

        Assert.Same(failure, Assert.Throws<IOException>(() => WalDirectoryDurability.Synchronize("retained", operations)));

        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void InterruptedLeaseAcquisitionRetriesExclusiveNonblockingLockingWithoutOwningTheHandle()
    {
        using SafeFileHandle handle = FakeHandle();
        Operations operations = new();
        operations.Locks.Enqueue(new WalDirectoryStatus(-1, 4));
        operations.Locks.Enqueue(new WalDirectoryStatus(0, 4));

        WalDirectoryDurability.AcquireLease(handle, operations);

        Assert.False(handle.IsClosed);
        Assert.Equal(new[] { "lock:6", "lock:6" }, operations.Trace);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(11)]
    public void RefusedOwnershipDoesNotRetryOtherErrorsOrCloseTheCallersHandle(int error)
    {
        using SafeFileHandle handle = FakeHandle();
        Operations operations = new();
        operations.Locks.Enqueue(new WalDirectoryStatus(-1, error));

        var failure = Assert.Throws<IOException>(() => WalDirectoryDurability.AcquireLease(handle, operations));

        Assert.Equal("Could not acquire exclusive WAL directory ownership.", failure.Message);
        Assert.Equal(error, Assert.IsType<Win32Exception>(failure.InnerException).NativeErrorCode);
        Assert.False(handle.IsClosed);
        Assert.Equal(new[] { "lock:6" }, operations.Trace);
    }

    [Fact]
    public void MissingOperationsOrLeaseHandlesAreRefusedBeforeAnySystemCall()
    {
        using SafeFileHandle handle = FakeHandle();
        Assert.Equal("operations", Assert.Throws<ArgumentNullException>(
            () => WalDirectoryDurability.Synchronize("directory", null!)).ParamName);
        Assert.Equal("operations", Assert.Throws<ArgumentNullException>(
            () => WalDirectoryDurability.AcquireLease(handle, null!)).ParamName);
        Assert.Equal("handle", Assert.Throws<ArgumentNullException>(
            () => WalDirectoryDurability.AcquireLease(null!, new Operations())).ParamName);
    }

    [Fact]
    public void UnsupportedPlatformValidationRunsBeforeAnyLeaseSystemCall()
    {
        using SafeFileHandle handle = FakeHandle();
        PlatformNotSupportedException failure = new("unsupported directory platform");
        Operations operations = new() { PlatformFailure = failure };

        Assert.Same(failure, Assert.Throws<PlatformNotSupportedException>(
            () => WalDirectoryDurability.AcquireLease(handle, operations)));
        Assert.Empty(operations.Trace);
        Assert.False(handle.IsClosed);
    }

    [Fact]
    public void FailedNativeOpeningReturnsItsErrorWithoutInventingAHandle()
    {
        PosixWalDirectoryOperations operations = new();
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing");

        WalDirectoryOpen result = operations.Open(missing, operations.OpenFlags);

        Assert.Null(result.Handle);
        Assert.Equal(2, result.Error);
    }

    [Fact]
    public void DescriptorZeroIsAValidSuccessfulOpenAndPreservesItsCapturedError()
    {
        WalDirectoryOpen result = PosixWalDirectoryOperations.OwnDescriptor(0, 17);
        try
        {
            Assert.NotNull(result.Handle);
            Assert.False(result.Handle.IsInvalid);
            Assert.Equal(IntPtr.Zero, result.Handle.DangerousGetHandle());
            Assert.Equal(17, result.Error);
        }
        finally
        {
            result.Handle?.SetHandleAsInvalid();
            result.Handle?.Dispose();
        }
    }

    [Fact]
    public void DisposingAnOpenedDirectoryReleasesItsNativeDescriptor()
    {
        string path = Path.Combine(Path.GetTempPath(), "directory-handle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        DateTime marker = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(path, marker);
        PosixWalDirectoryOperations operations = new();
        WalDirectoryOpen opened = operations.Open(path, operations.OpenFlags);
        Assert.NotNull(opened.Handle);
        IntPtr descriptor = opened.Handle.DangerousGetHandle();
        try
        {
            Assert.Equal(marker, File.GetLastWriteTimeUtc(opened.Handle));
            opened.Handle.Dispose();
            using SafeFileHandle borrowed = new(descriptor, ownsHandle: false);
            DateTime? retainedIdentity = null;
            try
            {
                retainedIdentity = File.GetLastWriteTimeUtc(borrowed);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            if (retainedIdentity == marker)
            {
                using SafeFileHandle cleanup = new(descriptor, ownsHandle: true);
            }
            Assert.NotEqual(marker, retainedIdentity);
        }
        finally
        {
            opened.Handle.Dispose();
            Directory.Delete(path);
        }
    }

    [Fact]
    public void NativeOperationsReportInvalidDescriptorErrorsWithoutInventingSuccess()
    {
        PosixWalDirectoryOperations operations = new();
        using SafeFileHandle invalid = new(new IntPtr(-1), ownsHandle: false);

        WalDirectoryStatus sync = operations.Synchronize(invalid);
        WalDirectoryStatus ownership = operations.Lock(invalid, 6);

        Assert.Equal(-1, sync.Value);
        Assert.Equal(9, sync.Error);
        Assert.Equal(-1, ownership.Value);
        Assert.Equal(9, ownership.Error);
    }

    [Fact]
    public void MissingAncestorsAreOrderedUntilTheFirstExistingDirectory()
    {
        string root = Path.GetPathRoot(Path.GetFullPath("."))!;
        string parent = Path.Combine(root, "parent");
        string child = Path.Combine(parent, "child");
        List<string> probes = [];

        var missing = WalDirectory.FindMissingDirectories(child, path =>
        {
            probes.Add(path);
            return path == root;
        });

        Assert.Equal(new[] { child, parent }, missing);
        Assert.Equal(new[] { child, parent, root }, probes);
        Assert.Empty(WalDirectory.FindMissingDirectories(child, _ => true));
        Assert.Equal(new[] { child, parent, root }, WalDirectory.FindMissingDirectories(child, _ => false));
    }

    private static SafeFileHandle FakeHandle() => new(new IntPtr(123), ownsHandle: false);

    private sealed class Operations : IWalDirectoryOperations
    {
        public Queue<WalDirectoryOpen> Opens { get; } = new();
        public Queue<WalDirectoryStatus> Synchronizations { get; } = new();
        public Queue<WalDirectoryStatus> Locks { get; } = new();
        public List<string> Trace { get; } = [];
        public int Flags { get; private set; }
        public IOException? SynchronizationFailure { get; init; }
        public PlatformNotSupportedException? PlatformFailure { get; init; }
        public int OpenFlags => PlatformFailure is null ? 1234 : throw PlatformFailure;

        public WalDirectoryOpen Open(string path, int flags)
        {
            Flags = flags;
            Trace.Add("open:" + path);
            return Opens.Dequeue();
        }

        public WalDirectoryStatus Synchronize(SafeFileHandle handle)
        {
            Assert.False(handle.IsClosed);
            Trace.Add("sync");
            if (SynchronizationFailure is not null)
            {
                throw SynchronizationFailure;
            }
            return Synchronizations.Dequeue();
        }

        public WalDirectoryStatus Lock(SafeFileHandle handle, int operation)
        {
            Assert.False(handle.IsClosed);
            Trace.Add("lock:" + operation);
            return Locks.Dequeue();
        }
    }
}

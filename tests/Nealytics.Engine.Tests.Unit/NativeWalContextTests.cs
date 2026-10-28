using System.ComponentModel;
using System.Runtime.InteropServices;
using Nealytics.Engine.Infrastructure.Storage.Wal.Native;

namespace Nealytics.Engine.Tests.Unit;

public sealed class NativeWalContextTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-native-context-" + Guid.NewGuid().ToString("N"));

    public NativeWalContextTests() => Directory.CreateDirectory(_directory);

    private string PathName => Path.Combine(_directory, "file.log");

    [Theory]
    [InlineData(true, Architecture.X64, true)]
    [InlineData(true, Architecture.Arm64, true)]
    [InlineData(true, Architecture.X86, false)]
    [InlineData(false, Architecture.X64, false)]
    [InlineData(false, Architecture.Arm64, false)]
    public void PlatformSelectionIsExplicit(bool linux, Architecture architecture, bool expected)
    {
        Assert.Equal(expected, NativeWalPlatform.IsSupported(linux, architecture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContextOwnsItsHandleAndPassesOperationsThroughTheNativeBoundary(bool registered)
    {
        TestApi api = new() { Registered = registered };
        LiburingWalContext context = new(PathName, 4096, 8, api);
        Assert.Equal(registered, context.IsFileRegistered);
        Assert.Equal(4096U, api.Capacity);
        Assert.Equal(8U, api.Entries);
        Assert.True(api.File >= 0);
        context.Append(new byte[] { 1, 2, 3 });
        context.Flush();
        context.Truncate();
        Assert.Equal([1, 2, 3], api.Record);
        Assert.Equal(1, api.AppendCalls);
        Assert.Equal(1, api.FlushCalls);
        Assert.Equal(1, api.TruncateCalls);
        context.Dispose();
        context.Dispose();
        Assert.Equal(1, api.DestroyCalls);
        Assert.Equal((nint)42, api.DestroyedHandle);
        Assert.Throws<ObjectDisposedException>(() => context.Append(new byte[1]));
        Assert.Throws<ObjectDisposedException>(() => context.Flush());
        Assert.Throws<ObjectDisposedException>(() => context.Truncate());
        using FileStream exclusive = new(PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(-1, 8)]
    [InlineData(4096, 7)]
    [InlineData(4096, 4097)]
    public void InvalidLimitsAreRejectedBeforeNativeAllocationOrFileCreation(int capacity, int entries)
    {
        TestApi api = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiburingWalContext(PathName, capacity, entries, api));
        Assert.False(File.Exists(PathName));
        Assert.Equal(0, api.CreateCalls);
    }

    [Fact]
    public void MissingApisAndIncompatibleAbisAreRefusedBeforeOpeningTheFile()
    {
        Assert.Throws<ArgumentNullException>(() => new LiburingWalContext(PathName, 4096, 8, null!));
        TestApi api = new() { Version = 2 };
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new LiburingWalContext(PathName, 4096, 8, api));
        Assert.Contains("ABI", error.Message);
        Assert.False(File.Exists(PathName));
        Assert.Equal(0, api.CreateCalls);
    }

    [Theory]
    [InlineData("create-status", false)]
    [InlineData("create-throw", false)]
    [InlineData("registration", true)]
    public void ConstructorFailuresReleaseEveryAcquiredResource(string operation, bool createdHandle)
    {
        TestApi api = new() { FailureOperation = operation };
        IOException error = Assert.Throws<IOException>(() => new LiburingWalContext(PathName, 4096, 8, api));
        Assert.Equal(createdHandle ? 1 : 0, api.DestroyCalls);
        if (operation != "create-status")
        {
            Assert.Same(api.Failure, error);
        }
        using FileStream exclusive = new(PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AReportedSuccessCannotReturnAnInvalidNativeHandle(int handle)
    {
        TestApi api = new() { CreatedHandle = handle };
        IOException error = Assert.Throws<IOException>(() => new LiburingWalContext(PathName, 4096, 8, api));
        Assert.Contains("invalid handle", error.Message);
        Assert.Equal(0, api.DestroyCalls);
        using FileStream exclusive = new(PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("flush")]
    [InlineData("truncate")]
    public void NegativeNativeStatusesAreReportedWithTheOperationAndErrno(string operation)
    {
        TestApi api = new() { FailureOperation = operation };
        using LiburingWalContext context = new(PathName, 4096, 8, api);
        IOException error = Assert.Throws<IOException>(() =>
        {
            switch (operation)
            {
                case "append": context.Append(new byte[1]); break;
                case "flush": context.Flush(); break;
                default: context.Truncate(); break;
            }
        });
        Assert.Contains(operation, error.Message);
        Assert.Contains("28", error.Message);
        Assert.Equal(28, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
    }

    [Fact]
    public void UnresolvedKernelOwnershipIsReportedDuringDisposalAndTheFileHandleIsClosed()
    {
        TestApi api = new() { DestroyStatus = -16 };
        LiburingWalContext context = new(PathName, 4096, 8, api);
        IOException error = Assert.Throws<IOException>(() => context.Dispose());
        Assert.Contains("process restart required", error.Message);
        Assert.Equal(16, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
        Assert.Equal(1, api.DestroyCalls);
        context.Dispose();
        Assert.Equal(1, api.DestroyCalls);
        using FileStream exclusive = new(PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private sealed class TestApi : INativeWalApi
    {
        public uint Version { get; init; } = 1;
        public int CreatedHandle { get; init; } = 42;
        public bool Registered { get; init; }
        public string FailureOperation { get; init; } = string.Empty;
        public IOException Failure { get; } = new("injected native API exception");
        public int DestroyStatus { get; init; }
        public int CreateCalls { get; private set; }
        public int DestroyCalls { get; private set; }
        public int AppendCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public int TruncateCalls { get; private set; }
        public int File { get; private set; }
        public uint Entries { get; private set; }
        public nuint Capacity { get; private set; }
        public nint DestroyedHandle { get; private set; }
        public byte[] Record { get; private set; } = [];

        public uint AbiVersion() => Version;

        public int Create(int file, uint entries, nuint capacity, out nint context)
        {
            CreateCalls++;
            File = file;
            Entries = entries;
            Capacity = capacity;
            context = FailureOperation == "create-status" ? 0 : CreatedHandle;
            if (FailureOperation == "create-throw")
            {
                throw Failure;
            }
            return FailureOperation == "create-status" ? -28 : 0;
        }

        public int Append(NativeWalHandle context, ReadOnlyMemory<byte> record)
        {
            Assert.False(context.IsInvalid);
            AppendCalls++;
            Record = record.ToArray();
            return FailureOperation == "append" ? -28 : 0;
        }

        public int Flush(NativeWalHandle context)
        {
            Assert.False(context.IsInvalid);
            FlushCalls++;
            return FailureOperation == "flush" ? -28 : 0;
        }

        public int Truncate(NativeWalHandle context)
        {
            Assert.False(context.IsInvalid);
            TruncateCalls++;
            return FailureOperation == "truncate" ? -28 : 0;
        }

        public int RegisteredFile(NativeWalHandle context)
        {
            Assert.False(context.IsInvalid);
            if (FailureOperation == "registration")
            {
                throw Failure;
            }
            return Registered ? 1 : 0;
        }

        public int Destroy(nint context)
        {
            DestroyCalls++;
            DestroyedHandle = context;
            return DestroyStatus;
        }
    }
}

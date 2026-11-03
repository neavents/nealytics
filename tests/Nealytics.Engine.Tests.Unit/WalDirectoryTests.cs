using Nealytics.Engine.Infrastructure.Storage.Wal;
using System.Runtime.InteropServices;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalDirectoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void InvalidDirectoryArgumentsIdentifyTheConfigurationInput(string? directory)
    {
        var error = Assert.ThrowsAny<ArgumentException>(() => new WalDirectory(directory!));
        Assert.Equal("directory", error.ParamName);
    }

    [Theory]
    [InlineData("openLease")]
    [InlineData("openActive")]
    [InlineData("acquireLease")]
    public void MissingFileOperationsFailBeforeCreatingTheDirectory(string missing)
    {
        using Harness harness = new();
        Func<string, FileStream> openLease = path => new FileStream(path, FileMode.OpenOrCreate);
        Func<string, FileStream> openActive = path => new FileStream(path, FileMode.Open);
        Action<Microsoft.Win32.SafeHandles.SafeFileHandle> acquireLease = _ => { };
        var error = Assert.Throws<ArgumentNullException>(() =>
        {
            using var directory = new WalDirectory(harness.Path, _ => { },
                missing == "openLease" ? null! : openLease,
                missing == "openActive" ? null! : openActive,
                missing == "acquireLease" ? null! : acquireLease);
        });
        Assert.Equal(missing, error.ParamName);
        Assert.False(Directory.Exists(harness.Path));
    }

    [Fact]
    public void LeaseAndActiveFileBarriersPrecedeDirectoryPublicationAndCoverEveryNewParent()
    {
        using Harness harness = new();
        Directory.CreateDirectory(harness.Path);
        string parent = Path.Combine(harness.Path, "parent");
        string child = Path.Combine(parent, "child");
        List<string> trace = [];
        using WalDirectory directory = new(child, path => trace.Add("sync:" + path),
            path => new ProbeFileStream(path, FileMode.OpenOrCreate, "lease", trace),
            path => new ProbeFileStream(path, FileMode.Open, "active", trace),
            _ => trace.Add("lock"));
        Assert.Equal(new[] { "lock", "lease:True", "sync:" + child, "sync:" + parent, "sync:" + harness.Path }, trace);
        File.WriteAllText(directory.ActivePath, "retained\n");
        trace.Clear();

        directory.SealActive();

        Assert.Equal(new[] { "active:True", "sync:" + child }, trace);
        Assert.Equal("retained\n", File.ReadAllText(Assert.Single(directory.RecoveryPaths())));
    }

    [Fact]
    public void ExhaustedGenerationsFailBeforeTheActiveFileIsTouched()
    {
        using Harness harness = new();
        using WalDirectory directory = new(harness.Path, _ => { });
        string last = Path.Combine(harness.Path, "telemetry_wal.replay." + long.MaxValue.ToString("D20"));
        File.WriteAllText(last, "older\n");
        File.WriteAllText(directory.ActivePath, "active\n");

        Assert.Throws<OverflowException>(directory.SealActive);

        Assert.Equal("active\n", File.ReadAllText(directory.ActivePath));
        Assert.Equal("older\n", File.ReadAllText(Assert.Single(directory.RecoveryPaths())));
    }

    [Fact]
    public void ReleasedOwnershipCannotDeleteARecoveryFile()
    {
        using Harness harness = new();
        WalDirectory directory = new(harness.Path, _ => { });
        File.WriteAllText(directory.ActivePath, "retained\n");
        directory.SealActive();
        string path = Assert.Single(directory.RecoveryPaths());
        directory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => directory.DeleteRecovery([path]));

        Assert.Equal("retained\n", File.ReadAllText(path));
    }

    [Fact]
    public void SegmentMeasurementsCountEveryTerminatorAcrossReadBuffersWithoutCountingATornTail()
    {
        using Harness harness = new();
        Directory.CreateDirectory(harness.Path);
        string path = Path.Combine(harness.Path, "segment");
        string contents = new string('x', 65_536) + "\n" + new string('y', 65_537) + "\ntorn";
        File.WriteAllText(path, contents);

        var measured = WalDirectory.Measure(path);

        Assert.Equal(contents.Length, measured.Bytes);
        Assert.Equal(2, measured.Records);
    }

    [Fact]
    public void RepeatedStartupNeverCopiesAnActiveFileOntoAPreviousTornTail()
    {
        using Harness harness = new();
        using WalDirectory directory = new(harness.Path, _ => { });
        File.WriteAllText(directory.ActivePath, "first\ntorn");
        directory.SealActive();
        File.WriteAllText(directory.ActivePath, "second\n");
        directory.SealActive();
        File.WriteAllText(directory.ActivePath, "third\n");
        directory.SealActive();
        string[] paths = directory.RecoveryPaths();
        Assert.Equal(3, paths.Length);
        Assert.Equal("first\ntorn", File.ReadAllText(paths[0]));
        Assert.Equal("second\n", File.ReadAllText(paths[1]));
        Assert.Equal("third\n", File.ReadAllText(paths[2]));
        Assert.EndsWith(".00000000000000000001", paths[1]);
        Assert.EndsWith(".00000000000000000002", paths[2]);
        directory.DeleteRecovery(paths);
        Assert.Empty(directory.RecoveryPaths());
    }

    [Fact]
    public void ASecondOwnerCannotTouchFilesUntilTheFirstOwnerHasDisposedItsLease()
    {
        using Harness harness = new();
        WalDirectory first = new(harness.Path, _ => { });
        File.WriteAllText(first.ActivePath, "record\n");
        Assert.Throws<IOException>(() => new WalDirectory(harness.Path, _ => { }));
        Assert.Equal("record\n", File.ReadAllText(first.ActivePath));
        first.Dispose();
        first.Dispose();
        using WalDirectory second = new(harness.Path, _ => { });
        second.SealActive();
        Assert.Single(second.RecoveryPaths());
        Assert.Throws<ObjectDisposedException>(first.Synchronize);
        Assert.Throws<ObjectDisposedException>(first.SealActive);
        Assert.Throws<ObjectDisposedException>(() => first.RecoveryPaths());
    }

    [Fact]
    public void FailedMetadataSynchronizationReleasesOwnershipAndPreservesTheRenamedFile()
    {
        using Harness harness = new();
        IOException failure = new("metadata failure");
        Assert.Same(failure, Assert.Throws<IOException>(() => new WalDirectory(harness.Path, _ => throw failure)));
        int calls = 0;
        using WalDirectory directory = new(harness.Path, _ =>
        {
            if (++calls == 2)
            {
                throw failure;
            }
        });
        File.WriteAllText(directory.ActivePath, "record\n");
        Assert.Same(failure, Assert.Throws<IOException>(directory.SealActive));
        Assert.Equal("record\n", File.ReadAllText(Assert.Single(directory.RecoveryPaths())));
        Assert.False(File.Exists(directory.ActivePath));
    }

    [Theory]
    [InlineData("telemetry_wal.replay.backup")]
    [InlineData("telemetry_wal.replay.00000000000000000000")]
    [InlineData("telemetry_wal.replay.0000000000000000000x")]
    public void UnknownRecoveryNamesFailBeforeAnActiveFileCanBeRenamed(string name)
    {
        using Harness harness = new();
        using WalDirectory directory = new(harness.Path, _ => { });
        File.WriteAllText(directory.ActivePath, "active\n");
        File.WriteAllText(System.IO.Path.Combine(harness.Path, name), "older\n");
        string expected = $"Unrecognized WAL recovery segment '{name}'.";
        Assert.Equal(expected, Assert.Throws<InvalidDataException>(() => directory.RecoveryPaths()).Message);
        Assert.Equal(expected, Assert.Throws<InvalidDataException>(directory.SealActive).Message);
        Assert.Equal("active\n", File.ReadAllText(directory.ActivePath));
    }

    [Fact]
    public void DeletionCannotTargetTheActiveFileOrAnotherDirectory()
    {
        using Harness harness = new();
        using Harness other = new();
        using WalDirectory directory = new(harness.Path, _ => { });
        File.WriteAllText(directory.ActivePath, "active\n");
        Assert.Throws<InvalidDataException>(() => directory.DeleteRecovery([directory.ActivePath]));
        string foreign = System.IO.Path.Combine(other.Path, "telemetry_wal.replay");
        Directory.CreateDirectory(other.Path);
        File.WriteAllText(foreign, "foreign\n");
        var error = Assert.Throws<ArgumentException>(() => directory.DeleteRecovery([foreign]));
        Assert.Equal("paths", error.ParamName);
        Assert.Contains("Recovery files must belong to the owned WAL directory.", error.Message);
        Assert.Equal("foreign\n", File.ReadAllText(foreign));
        Assert.Equal("active\n", File.ReadAllText(directory.ActivePath));
    }

    [Fact]
    public void RealDirectorySynchronizationUsesTheHostFilesystemAndReportsMissingPaths()
    {
        using Harness harness = new();
        using WalDirectory directory = new(harness.Path);
        directory.Synchronize();
        File.WriteAllText(directory.ActivePath, "record\n");
        directory.SealActive();
        directory.DeleteRecovery(directory.RecoveryPaths());
        Assert.Throws<IOException>(() => WalDirectoryDurability.Synchronize(System.IO.Path.Combine(harness.Path, "missing")));
    }

    [Theory]
    [InlineData(true, false, Architecture.X64, 0x0009_0000)]
    [InlineData(true, false, Architecture.Arm64, 0x0008_4000)]
    [InlineData(false, true, Architecture.X64, 0x0110_0000)]
    [InlineData(false, true, Architecture.Arm64, 0x0110_0000)]
    public void DirectoryFlagsMatchTheSupportedKernelAndArchitecture(bool linux, bool macOS, Architecture architecture, int expected)
    {
        Assert.Equal(expected, WalDirectoryDurability.OpenFlags(linux, macOS, architecture));
    }

    [Theory]
    [InlineData(true, false, Architecture.Arm)]
    [InlineData(false, false, Architecture.X64)]
    public void UnverifiedDirectorySynchronizationPlatformsFailClosed(bool linux, bool macOS, Architecture architecture)
    {
        var error = Assert.Throws<PlatformNotSupportedException>(
            () => WalDirectoryDurability.OpenFlags(linux, macOS, architecture));
        Assert.Equal(linux
            ? "WAL directory durability requires a verified host architecture."
            : "WAL directory durability requires a verified directory synchronization backend.", error.Message);
    }

    private sealed class Harness : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nealytics-directory-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

    private sealed class ProbeFileStream(string path, FileMode mode, string label, List<string> trace)
        : FileStream(path, mode, FileAccess.ReadWrite, FileShare.None)
    {
        public override void Flush(bool flushToDisk)
        {
            if (flushToDisk)
            {
                trace.Add(label + ":True");
            }
            base.Flush(flushToDisk);
        }
    }
}

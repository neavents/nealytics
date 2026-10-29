using Nealytics.Engine.Infrastructure.Storage.Wal;
using System.Runtime.InteropServices;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalDirectoryTests
{
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
        Assert.Throws<InvalidDataException>(directory.SealActive);
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
        Assert.Throws<ArgumentException>(() => directory.DeleteRecovery([foreign]));
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
        Assert.Throws<PlatformNotSupportedException>(() => WalDirectoryDurability.OpenFlags(linux, macOS, architecture));
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
}

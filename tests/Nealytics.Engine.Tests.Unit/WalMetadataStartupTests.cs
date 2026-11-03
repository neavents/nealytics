using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalMetadataStartupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverflowingRecoveryMeasurementsFailBeforeOpeningAnActiveFileAndReleaseOwnership(bool bytes)
    {
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-recovery-overflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string first = Path.Combine(directory, "telemetry_wal.replay");
            string second = first + ".00000000000000000001";
            File.WriteAllText(first, "retained first segment");
            File.WriteAllText(second, "retained second segment");
            int measured = 0;
            Assert.Throws<OverflowException>(() => new WriteAheadLogger(Options.Create(new TelemetryEngineOptions
            {
                WriteAheadLogDirectory = directory
            }), file: null, measureRecovery: _ =>
            {
                long value = measured++ == 0 ? long.MaxValue : 1;
                return bytes ? new WalDirectory.SegmentSize(value, 0) : new WalDirectory.SegmentSize(0, value);
            }));
            Assert.Equal(2, measured);
            Assert.False(File.Exists(Path.Combine(directory, "telemetry_wal.log")));
            Assert.Equal("retained first segment", File.ReadAllText(first));
            Assert.Equal("retained second segment", File.ReadAllText(second));
            using WalDirectory ownership = new(directory, _ => { });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TheActiveFileCreationIsSynchronizedBeforeAppendAdmission()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-metadata-order-" + Guid.NewGuid().ToString("N"));
        List<bool> activeAtSynchronization = [];
        try
        {
            await using WriteAheadLogger wal = new(Options.Create(new TelemetryEngineOptions
            {
                WriteAheadLogDirectory = directory
            }), file: null, directorySynchronize: path =>
            {
                if (path == directory)
                {
                    activeAtSynchronization.Add(File.Exists(Path.Combine(directory, "telemetry_wal.log")));
                }
            });
            Assert.Equal(new[] { false, true }, activeAtSynchronization);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AFailedActiveFileMetadataBarrierClosesItsFileAndDirectoryOwnership()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-metadata-failure-" + Guid.NewGuid().ToString("N"));
        IOException failure = new("active metadata failure");
        try
        {
            Assert.Same(failure, Assert.Throws<IOException>(() => new WriteAheadLogger(Options.Create(new TelemetryEngineOptions
            {
                WriteAheadLogDirectory = directory
            }), file: null, directorySynchronize: path =>
            {
                if (path == directory && File.Exists(Path.Combine(directory, "telemetry_wal.log")))
                {
                    throw failure;
                }
            })));
            using WalDirectory ownership = new(directory, _ => { });
            using FileStream active = new(ownership.ActivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(0, active.Length);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
    [Fact]
    public async Task FailedDeliveryMetadataCleanupStopsNewAcknowledgmentsAndPreservesItsOriginalFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-checkpoint-failure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new TelemetryEngineOptions { WriteAheadLogDirectory = directory });
            await using var wal = new WriteAheadLogger(options);
            IOException failure = new("delivery checkpoint failed");
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
                wal.CheckpointDeliveryAsync(() => throw failure)));
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => wal.AppendAsync(
                new Nealytics.Engine.Infrastructure.Serialization.GlobalTelemetryPayload
                {
                    ProjectId = "p", TenantId = "t", EventType = "e"
                }, CancellationToken.None)));
            Assert.Equal(0, wal.UncommittedRecordCount);
            Assert.Equal(0, wal.PendingByteCount);
            bool repeated = false;
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
                wal.CheckpointDeliveryAsync(() => repeated = true)));
            Assert.False(repeated);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

}

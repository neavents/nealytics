using System.Text.Json;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalSegmentRecoveryTests
{
    [Fact]
    public async Task RepeatedRestartsPreserveOrderedSegmentsAndNeverJoinATornTailToTheNextRecord()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-segment-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            GlobalTelemetryPayload first = Event();
            GlobalTelemetryPayload second = Event();
            GlobalTelemetryPayload third = Event();
            string legacy = Path.Combine(directory, "telemetry_wal.replay");
            string active = Path.Combine(directory, "telemetry_wal.log");
            string original = Frame(first) + "{\"torn\":";
            await File.WriteAllTextAsync(legacy, original);
            await File.WriteAllTextAsync(active, Frame(second));
            IOptions<TelemetryEngineOptions> options = Options.Create(new TelemetryEngineOptions
            {
                WriteAheadLogDirectory = directory
            });
            await using (WriteAheadLogger firstRestart = new(options))
            {
                IReadOnlyList<GlobalTelemetryPayload> recovered = await firstRestart.ReplayUncommittedAsync();
                Assert.Equal(new[] { first.EventId, second.EventId }, recovered.Select(item => item.EventId));
                await firstRestart.AppendAsync(third, CancellationToken.None);
            }
            await using WriteAheadLogger secondRestart = new(options);
            IReadOnlyList<GlobalTelemetryPayload> afterRestart = await secondRestart.ReplayUncommittedAsync();
            Assert.Equal(new[] { first.EventId, second.EventId, third.EventId }, afterRestart.Select(item => item.EventId));
            Assert.Equal(original, await File.ReadAllTextAsync(legacy));
            Assert.Equal(3, Directory.GetFiles(directory, "telemetry_wal.replay*").Length);
            await secondRestart.DeleteSealedSegmentAsync();
            Assert.False(secondRestart.HasSealedSegment);
            Assert.Empty(await secondRestart.ReplayUncommittedAsync());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task AnotherWalOwnerFailsWithoutSealingTheActiveFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-segment-owner-" + Guid.NewGuid().ToString("N"));
        IOptions<TelemetryEngineOptions> options = Options.Create(new TelemetryEngineOptions
        {
            WriteAheadLogDirectory = directory
        });
        try
        {
            await using WriteAheadLogger first = new(options);
            GlobalTelemetryPayload payload = Event();
            await first.AppendAsync(payload, CancellationToken.None);
            Assert.Throws<IOException>(() => new WriteAheadLogger(options));
            Assert.Empty(Directory.GetFiles(directory, "telemetry_wal.replay*"));
            Assert.Equal(Frame(payload), await File.ReadAllTextAsync(Path.Combine(directory, "telemetry_wal.log")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryStatusChangesOnlyAfterItsDirectoryCheckpointIsDurable(bool failBarrier)
    {
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-recovery-status-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new TelemetryEngineOptions { WriteAheadLogDirectory = directory });
        try
        {
            await using (var seed = new WriteAheadLogger(options))
            {
                await seed.AppendAsync(Event(), CancellationToken.None);
            }
            bool deleting = false;
            IOException failure = new("recovery directory barrier failed");
            WriteAheadLogger? owned = null;
            await using var wal = new WriteAheadLogger(options, file: null, directorySynchronize: path =>
            {
                if (deleting)
                {
                    Assert.True(owned!.HasSealedSegment);
                    Assert.Empty(Directory.GetFiles(directory, "telemetry_wal.replay*"));
                    if (failBarrier)
                    {
                        throw failure;
                    }
                }
                Nealytics.Engine.Infrastructure.Storage.Wal.WalDirectoryDurability.Synchronize(path);
            });
            owned = wal;
            Assert.True(wal.HasSealedSegment);
            deleting = true;
            if (failBarrier)
            {
                Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => wal.DeleteSealedSegmentAsync()));
                Assert.True(wal.HasSealedSegment);
                await Assert.ThrowsAsync<IOException>(() => wal.AppendAsync(Event(), CancellationToken.None));
            }
            else
            {
                await wal.DeleteSealedSegmentAsync();
                Assert.False(wal.HasSealedSegment);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static GlobalTelemetryPayload Event() => new()
    {
        ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e"
    };

    private static string Frame(GlobalTelemetryPayload payload) =>
        JsonSerializer.Serialize(payload, TelemetryAotContext.Default.GlobalTelemetryPayload) + "\n";
}

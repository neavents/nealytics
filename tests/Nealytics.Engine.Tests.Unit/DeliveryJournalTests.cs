using System.Text;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Delivery;

namespace Nealytics.Engine.Tests.Unit;

public sealed class DeliveryJournalTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-delivery-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _source = Guid.NewGuid();

    private TelemetryEngineOptions Settings() => new()
    {
        WriteAheadLogDirectory = _directory,
        EnableAsyncInsert = false
    };

    private TelemetryDeliveryUnit Unit(GlobalTelemetryPayload[]? items = null) => new()
    {
        SourceId = _source,
        Schema = new string('A', 64),
        Items = items ?? [Payload()]
    };

    private static GlobalTelemetryPayload Payload() => new()
    {
        WalRecordId = Guid.NewGuid(), ProjectId = "delivery-test", TenantId = "tenant",
        EventType = "event", MetadataJson = "{\"key\":1}", Timestamp = DateTime.UtcNow
    };

    [Fact]
    public void PreparationFreezesEveryMutableFieldAndPersistsAValidatedUnit()
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        var original = Unit();
        var stored = journal.Prepare(original);
        original.Items[0].EventId = Guid.NewGuid();
        original.Items[0].MetadataJson = "changed";
        original.Items[0].Timestamp = DateTime.MinValue;
        var recovered = Assert.Single(new TelemetryDeliveryJournal(Options.Create(Settings())).Load());
        Assert.Equal(stored.Id, recovered.Id);
        Assert.Equal(stored.SourceId, recovered.SourceId);
        Assert.Equal(stored.Schema, recovered.Schema);
        Assert.Equal(stored.IngestedAt, recovered.IngestedAt);
        Assert.Equal(stored.Items[0].EventId, recovered.Items[0].EventId);
        Assert.Equal(stored.Items[0].Timestamp, recovered.Items[0].Timestamp);
        Assert.Equal("{\"key\":1}", recovered.Items[0].MetadataJson);
        Assert.NotSame(original.Items[0], stored.Items[0]);
        Assert.False(journal.IsDelivered(stored.Items[0]));
    }

    [Fact]
    public void ACompletedUnitIsRememberedAcrossRestartAndClearedOnlyAtCheckpoint()
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        var stored = journal.Prepare(Unit());
        journal.Complete(stored);
        Assert.True(journal.IsDelivered(stored.Items[0]));
        var restart = new TelemetryDeliveryJournal(Options.Create(Settings()));
        Assert.Empty(restart.Load());
        Assert.True(restart.IsDelivered(stored.Items[0]));
        restart.Clear();
        Assert.False(restart.IsDelivered(stored.Items[0]));
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "delivery")));
        restart.Prepare(Unit());
        Assert.Single(restart.Load());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(66)]
    public void ChangedManifestBytesAreRefused(int offset)
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        journal.Prepare(Unit());
        string path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "delivery"), "*.json"));
        byte[] bytes = File.ReadAllBytes(path);
        bytes[offset] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => journal.Load().ToArray());
    }

    [Fact]
    public void RenamedManifestIdentityIsRefused()
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        journal.Prepare(Unit());
        string path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "delivery"), "*.json"));
        File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".json"));
        Assert.Throws<InvalidDataException>(() => journal.Load().ToArray());
    }

    [Fact]
    public void AnUnpublishedTemporaryManifestCannotBecomeADelivery()
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        string path = Path.Combine(_directory, "delivery", Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, "torn");
        Assert.Empty(journal.Load());
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void InvalidManifestHeadersAndMembershipAreRefusedBeforePublication(int problem)
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        var unit = new TelemetryDeliveryUnit
        {
            Version = problem == 0 ? 2 : 1,
            Id = problem == 1 ? Guid.Empty : Guid.NewGuid(),
            SourceId = problem == 2 ? Guid.Empty : _source,
            Schema = problem == 3 ? "bad" : new string('A', 64),
            IngestedAt = problem == 4 ? default : DateTimeOffset.UtcNow,
            Items = problem switch
            {
                5 => [],
                6 => [new GlobalTelemetryPayload()],
                7 => [null!],
                _ => [Payload()]
            }
        };
        Assert.Throws<InvalidDataException>(() => journal.Prepare(unit));
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "delivery")));
    }

    [Fact]
    public void DuplicateMembershipIsRefusedWithinAndAcrossUnits()
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        var item = Payload();
        Assert.Throws<InvalidDataException>(() => journal.Prepare(Unit([item, item])));
        var first = journal.Prepare(Unit([item]));
        journal.Complete(first);
        var second = journal.Prepare(Unit([item]));
        Assert.Throws<InvalidDataException>(() => journal.Complete(second));
        Assert.Throws<InvalidDataException>(() => journal.Load().ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JournalLimitsApplyBeforePublicationAndOnRecovery(bool records)
    {
        var options = Settings();
        var writer = new TelemetryDeliveryJournal(Options.Create(options));
        writer.Prepare(Unit());
        options.DeliveryJournalMaxRecords = records ? 1 : 100000;
        options.DeliveryJournalMaxBytes = records ? 268435456 : 1;
        var limited = new TelemetryDeliveryJournal(Options.Create(options));
        if (records)
        {
            Assert.Single(limited.Load());
            Assert.Throws<InvalidDataException>(() => limited.Prepare(Unit()));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => limited.Load().ToArray());
        }
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "delivery"), "*.json"));
    }

    [Fact]
    public async Task RetryAndRestartKeepTheOriginalTokenMembershipAndIngestionTimestamp()
    {
        var options = Options.Create(Settings());
        var items = new[] { Payload(), Payload(), Payload() };
        var target = new RecordingWriter(_source, _directory) { Fail = true };
        await using (var wal = new WriteAheadLogger(options))
        {
            foreach (var item in items)
            {
                await wal.AppendAsync(item, CancellationToken.None);
            }
            var writer = new JournaledTelemetryBatchWriter(target, new TelemetryDeliveryJournal(options), wal, options);
            await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(items, items.Length, CancellationToken.None));
            await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(items, items.Length, CancellationToken.None));
            Assert.Equal(target.Units[0].Id, target.Units[1].Id);
            Assert.Equal(target.Units[0].IngestedAt, target.Units[1].IngestedAt);
            await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync([items[1]], 1, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(items.Reverse().ToArray(), 3, CancellationToken.None));
        }
        target.Fail = false;
        await using (var wal = new WriteAheadLogger(options))
        {
            var recovered = await wal.ReplayUncommittedAsync();
            var writer = new JournaledTelemetryBatchWriter(target, new TelemetryDeliveryJournal(options), wal, options);
            await writer.WriteAsync([recovered[1]], 1, CancellationToken.None);
            var replay = target.Units[^1];
            Assert.Equal(target.Units[0].Id, replay.Id);
            Assert.Equal(target.Units[0].IngestedAt, replay.IngestedAt);
            Assert.Equal(items.Select(item => item.WalRecordId), replay.Items.Select(item => item.WalRecordId));
            Assert.Equal(3, target.Units.Count);
            await writer.WriteAsync(recovered, recovered.Count, CancellationToken.None);
            Assert.Equal(3, target.Units.Count);
            await writer.CheckpointAsync();
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(_directory, "delivery")));
            await wal.DeleteSealedSegmentAsync();
            await writer.CheckpointAsync();
            Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "delivery")));
        }
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("storage")]
    [InlineData("expired")]
    [InlineData("future")]
    public async Task UnsafePendingUnitsNeverReachTheDatabase(string problem)
    {
        var options = Options.Create(Settings());
        await using var wal = new WriteAheadLogger(options);
        var unit = new TelemetryDeliveryUnit
        {
            SourceId = problem == "storage" ? Guid.NewGuid() : _source,
            Schema = new string(problem == "schema" ? 'B' : 'A', 64),
            IngestedAt = problem == "expired" ? DateTimeOffset.UtcNow.AddHours(-1)
                : problem == "future" ? DateTimeOffset.UtcNow.AddHours(1) : DateTimeOffset.UtcNow,
            Items = [Payload()]
        };
        var journal = new TelemetryDeliveryJournal(options);
        journal.Prepare(unit);
        var target = new RecordingWriter(_source, _directory);
        var writer = new JournaledTelemetryBatchWriter(target, journal, wal, options);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(unit.Items, 1, CancellationToken.None));
        Assert.Empty(target.Units);
        Assert.Single(journal.Load());
    }

    [Fact]
    public async Task SuccessfulDeliveryCannotCheckpointAnUncommittedWal()
    {
        var options = Options.Create(Settings());
        await using var wal = new WriteAheadLogger(options);
        var item = Payload();
        await wal.AppendAsync(item, CancellationToken.None);
        var writer = new JournaledTelemetryBatchWriter(new RecordingWriter(_source, _directory), new TelemetryDeliveryJournal(options), wal, options);
        await writer.WriteAsync([item], 1, CancellationToken.None);
        await writer.CheckpointAsync();
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_directory, "delivery")).Length);
        await wal.AcknowledgeCommitAsync(1);
        await writer.CheckpointAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "delivery")));
    }

    [Fact]
    public void DurableManifestAndCompletionBarriersPrecedeTheirDirectoryPublication()
    {
        var settings = Options.Create(Settings());
        List<string> trace = [];
        var journal = new TelemetryDeliveryJournal(settings,
            (path, mode) => new ProbeFileStream(path, mode, trace),
            path => trace.Add("sync:" + Path.GetFileName(path)));
        trace.Clear();
        var unit = journal.Prepare(Unit());
        Assert.Equal(new[] { "flush:True", "closed", "sync:delivery" }, trace);
        Assert.True(File.Exists(Path.Combine(_directory, "delivery", unit.Id.ToString("N") + ".json")));
        trace.Clear();
        journal.Complete(unit);
        Assert.Equal(new[] { "flush:True", "closed", "sync:delivery" }, trace);
        Assert.True(File.Exists(Path.Combine(_directory, "delivery", unit.Id.ToString("N") + ".committed")));
        trace.Clear();
        journal.Clear();
        Assert.Equal(new[] { "sync:delivery" }, trace);
    }

    [Fact]
    public async Task FailedManifestPublicationCannotCreateAnotherUnitOrReachTheDatabaseBeforeRestart()
    {
        var options = Options.Create(Settings());
        await using var wal = new WriteAheadLogger(options);
        var item = Payload();
        await wal.AppendAsync(item, CancellationToken.None);
        IOException failure = new("manifest directory barrier failed");
        var journal = new TelemetryDeliveryJournal(options,
            (path, mode) => new FileStream(path, mode, FileAccess.Write, FileShare.None),
            path =>
            {
                if (Path.GetFileName(path) == "delivery")
                {
                    throw failure;
                }
            });
        var target = new RecordingWriter(_source, _directory);
        var writer = new JournaledTelemetryBatchWriter(target, journal, wal, options);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync([item], 1, CancellationToken.None)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync([item], 1, CancellationToken.None)));
        Assert.Empty(target.Units);
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "delivery"), "*.json"));
        var restart = new JournaledTelemetryBatchWriter(target, new TelemetryDeliveryJournal(options), wal, options);
        await restart.WriteAsync([item], 1, CancellationToken.None);
        Assert.Single(target.Units);
    }

    [Fact]
    public void AggregateJournalByteCapacityCountsEveryManifestBeforeRecoveryAllocatesAnother()
    {
        var settings = Settings();
        var journal = new TelemetryDeliveryJournal(Options.Create(settings));
        journal.Prepare(Unit());
        journal.Prepare(Unit());
        settings.DeliveryJournalMaxBytes = Directory.GetFiles(Path.Combine(_directory, "delivery"), "*.json")
            .Sum(path => new FileInfo(path).Length) - 1;
        var limited = new TelemetryDeliveryJournal(Options.Create(settings));
        Assert.Throws<InvalidDataException>(() => limited.Load().ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsupportedDeliveryConfigurationIsRefused(bool asyncInsert)
    {
        var settings = Settings();
        settings.EnableAsyncInsert = asyncInsert;
        settings.DeliveryRetryMaxAgeSeconds = asyncInsert ? 900 : 0;
        var options = Options.Create(settings);
        await using var wal = new WriteAheadLogger(options);
        var journal = new TelemetryDeliveryJournal(options);
        if (asyncInsert)
        {
            Assert.Throws<InvalidOperationException>(() => new JournaledTelemetryBatchWriter(
                new RecordingWriter(_source, _directory), journal, wal, options));
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new JournaledTelemetryBatchWriter(
                new RecordingWriter(_source, _directory), journal, wal, options));
        }
    }

    [Fact]
    public async Task RecordCapacityRefusesNewAppendsUntilTheCurrentGenerationCanDrainAndCheckpoint()
    {
        var settings = Settings();
        settings.DeliveryJournalMaxRecords = 1;
        var options = Options.Create(settings);
        await using var wal = new WriteAheadLogger(options);
        var target = new RecordingWriter(_source, _directory);
        var writer = new JournaledTelemetryBatchWriter(target, new TelemetryDeliveryJournal(options), wal, options);
        for (int iteration = 0; iteration < 20; iteration++)
        {
            var item = Payload();
            await wal.AppendAsync(item, CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => wal.AppendAsync(Payload(), CancellationToken.None));
            await writer.WriteAsync([item], 1, CancellationToken.None);
            await wal.AcknowledgeCommitAsync(1);
            await writer.CheckpointAsync();
            Assert.Equal(0, wal.RetainedWalBytes);
            Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "delivery")));
        }
        Assert.Equal(20, target.Units.Count);
        Assert.Equal(20, target.Units.Select(unit => unit.Id).Distinct().Count());
    }

    [Fact]
    public async Task RecoveryCheckpointCanReleaseOldManifestsWhileNewActiveRecordsRemainUncommitted()
    {
        var settings = Settings();
        settings.DeliveryJournalMaxRecords = 2;
        var options = Options.Create(settings);
        await using (var seed = new WriteAheadLogger(options))
        {
            await seed.AppendAsync(Payload(), CancellationToken.None);
        }
        await using var wal = new WriteAheadLogger(options);
        var old = Assert.Single(await wal.ReplayUncommittedAsync());
        var live = Payload();
        await wal.AppendAsync(live, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => wal.AppendAsync(Payload(), CancellationToken.None));
        var target = new RecordingWriter(_source, _directory);
        var writer = new JournaledTelemetryBatchWriter(target, new TelemetryDeliveryJournal(options), wal, options);
        await writer.WriteAsync([old], 1, CancellationToken.None);
        await wal.DeleteSealedSegmentAsync();
        await writer.CheckpointRecoveryAsync();
        Assert.Equal(1, wal.UncommittedRecordCount);
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "delivery")));
        await writer.WriteAsync([live], 1, CancellationToken.None);
        await wal.AcknowledgeCommitAsync(1);
        await writer.CheckpointAsync();
        Assert.Equal(new[] { old.WalRecordId, live.WalRecordId }, target.Units.SelectMany(unit => unit.Items).Select(item => item.WalRecordId));
        Assert.Equal(0, wal.RetainedWalBytes);
    }

    [Fact]
    public async Task TheCompletionIndexLoadsOnceAndAvoidsReopeningPriorManifestsForEveryBatch()
    {
        var options = Options.Create(Settings());
        await using var wal = new WriteAheadLogger(options);
        var first = Payload();
        var second = Payload();
        await wal.AppendAsync(first, CancellationToken.None);
        await wal.AppendAsync(second, CancellationToken.None);
        new TelemetryDeliveryJournal(options).Prepare(Unit([first]));
        int reads = 0;
        var journal = new TelemetryDeliveryJournal(options,
            (path, mode) => new FileStream(path, mode, FileAccess.Write, FileShare.None),
            _ => { },
            path =>
            {
                reads++;
                return File.ReadAllBytes(path);
            });
        var target = new RecordingWriter(_source, _directory);
        var writer = new JournaledTelemetryBatchWriter(target, journal, wal, options);
        await writer.WriteAsync([first], 1, CancellationToken.None);
        await writer.WriteAsync([second], 1, CancellationToken.None);
        Assert.Equal(1, reads);
        Assert.Equal(2, target.Units.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(65)]
    public void TruncatedManifestsAreRefusedBeforeDeserialization(int bytes)
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        journal.Prepare(Unit());
        string path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "delivery"), "*.json"));
        File.WriteAllBytes(path, new byte[bytes]);
        Assert.Throws<InvalidDataException>(() => journal.Load().ToArray());
    }

    [Fact]
    public void AnEmptyButChecksummedManifestCannotBecomeADelivery()
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        journal.Prepare(Unit());
        string path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "delivery"), "*.json"));
        byte[] content = Encoding.UTF8.GetBytes("null");
        string header = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)) + "\n";
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(header).Concat(content).ToArray());
        Assert.Throws<InvalidDataException>(() => journal.Load().ToArray());
    }

    [Fact]
    public void AFullJournalCannotPublishAnotherManifest()
    {
        var settings = Settings();
        new TelemetryDeliveryJournal(Options.Create(settings)).Prepare(Unit());
        settings.DeliveryJournalMaxBytes = new FileInfo(Assert.Single(
            Directory.GetFiles(Path.Combine(_directory, "delivery"), "*.json"))).Length;
        var journal = new TelemetryDeliveryJournal(Options.Create(settings));
        Assert.Single(journal.Load());
        Assert.Throws<InvalidDataException>(() => journal.Prepare(Unit()));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "delivery"), "*.json"));
    }

    [Fact]
    public async Task AnUncertainViewOutcomeDisablesRollupRoutingUntilVerifiedStartup()
    {
        var settings = Settings();
        settings.Rollups = [new RollupOptions { Name = "delivery" }];
        var options = Options.Create(settings);
        await using var wal = new WriteAheadLogger(options);
        var dimensions = new DimensionRegistry(settings);
        var rollups = new RollupRegistry(settings, dimensions, new MeasureRegistry(settings, dimensions));
        var rollup = Assert.Single(rollups.Declared);
        rollups.MarkRoutable(rollup);
        var item = Payload();
        await wal.AppendAsync(item, CancellationToken.None);
        var target = new RecordingWriter(_source, _directory) { Fail = true };
        var writer = new JournaledTelemetryBatchWriter(target, new TelemetryDeliveryJournal(options), wal, options, rollups);
        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync([item], 1, CancellationToken.None));
        Assert.False(rollups.IsRoutable(rollup));
        Assert.Contains("uncertain", rollups.UnroutableReason(rollup));
        target.Fail = false;
        await writer.WriteAsync([item], 1, CancellationToken.None);
        Assert.False(rollups.IsRoutable(rollup));
    }

    private sealed class ProbeFileStream(string path, FileMode mode, List<string> trace)
        : FileStream(path, mode, FileAccess.Write, FileShare.None)
    {
        public override void Flush(bool flushToDisk)
        {
            trace.Add("flush:" + flushToDisk);
            base.Flush(flushToDisk);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            trace.Add("closed");
        }
    }

    private sealed class RecordingWriter(Guid source, string directory) : IDeliveryBatchWriter
    {
        public bool Fail;
        public List<TelemetryDeliveryUnit> Units { get; } = [];
        public string SchemaFingerprint => new('A', 64);
        public Task<Guid> GetSourceIdAsync(CancellationToken cancellationToken) => Task.FromResult(source);

        public Task WriteDeliveryAsync(TelemetryDeliveryUnit unit, CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(Path.Combine(directory, "delivery", unit.Id.ToString("N") + ".json")));
            Units.Add(unit);
            return Fail ? Task.FromException(new IOException("ambiguous insert")) : Task.CompletedTask;
        }

    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
        return ValueTask.CompletedTask;
    }
}

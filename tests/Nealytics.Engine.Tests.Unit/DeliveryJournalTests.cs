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

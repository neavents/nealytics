using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Delivery;
using NSubstitute;

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
        Assert.Empty(journal.Load());
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
        Assert.Contains("checksum does not match", Assert.Throws<InvalidDataException>(() => journal.Load().ToArray()).Message);
    }

    [Fact]
    public void RenamedManifestIdentityIsRefused()
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        journal.Prepare(Unit());
        string path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "delivery"), "*.json"));
        File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".json"));
        Assert.Contains("identity does not match its file", Assert.Throws<InvalidDataException>(() => journal.Load().ToArray()).Message);
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
        Assert.Contains(problem >= 6 ? "missing or duplicate record identities" : "header is invalid",
            Assert.Throws<InvalidDataException>(() => journal.Prepare(unit)).Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "delivery")));
    }

    [Fact]
    public void DuplicateMembershipIsRefusedWithinAndAcrossUnits()
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        var item = Payload();
        Assert.Contains("missing or duplicate record identities",
            Assert.Throws<InvalidDataException>(() => journal.Prepare(Unit([item, item]))).Message);
        var first = journal.Prepare(Unit([item]));
        journal.Complete(first);
        var second = journal.Prepare(Unit([item]));
        Assert.Contains("multiple committed delivery units", Assert.Throws<InvalidDataException>(() => journal.Complete(second)).Message);
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
            Assert.Contains("DeliveryJournalMaxRecords", Assert.Throws<InvalidDataException>(() => limited.Prepare(Unit())).Message);
        }
        else
        {
            Assert.Contains("recovery byte limit", Assert.Throws<InvalidDataException>(() => limited.Load().ToArray()).Message);
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
            Assert.Contains("must finish before another unit", (await Assert.ThrowsAsync<InvalidDataException>(() =>
                writer.WriteAsync([items[1]], 1, CancellationToken.None))).Message);
            Assert.Contains("cannot be regrouped", (await Assert.ThrowsAsync<InvalidDataException>(() =>
                writer.WriteAsync(items.Reverse().ToArray(), 3, CancellationToken.None))).Message);
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
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(unit.Items, 1, CancellationToken.None));
        Assert.Contains(problem is "schema" or "storage" ? "storage or schema changed" : "retry safety window expired", error.Message);
        Assert.Empty(target.Units);
        Assert.Single(journal.Load());
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(8_999_999_999L, true)]
    [InlineData(9_000_000_000L, false)]
    [InlineData(9_000_000_001L, false)]
    public async Task ReplayExpiryAndFutureTimestampsUseOneExplicitClockBoundary(long ageTicks, bool accepted)
    {
        var options = Options.Create(Settings());
        await using var wal = new WriteAheadLogger(options);
        var now = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        var unit = new TelemetryDeliveryUnit
        {
            SourceId = _source,
            Schema = new string('A', 64),
            IngestedAt = now.AddTicks(-ageTicks),
            Items = [Payload()]
        };
        var journal = new TelemetryDeliveryJournal(options);
        journal.Prepare(unit);
        var target = new RecordingWriter(_source, _directory);
        var writer = new JournaledTelemetryBatchWriter(target, journal, wal, options, clock: new FixedClock(now));
        if (accepted)
        {
            await writer.WriteAsync(unit.Items, 1, CancellationToken.None);
            Assert.Equal(unit.Id, Assert.Single(target.Units).Id);
        }
        else
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(unit.Items, 1, CancellationToken.None));
            Assert.Contains("retry safety window expired", error.Message);
            Assert.Empty(target.Units);
        }
    }

    [Fact]
    public async Task ANewDeliveryFreezesTheConfiguredClockTimeBeforeItsInsert()
    {
        var options = Options.Create(Settings());
        await using var wal = new WriteAheadLogger(options);
        var target = new RecordingWriter(_source, _directory);
        var now = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        var writer = new JournaledTelemetryBatchWriter(target, new TelemetryDeliveryJournal(options), wal, options,
            clock: new FixedClock(now));
        await writer.WriteAsync([Payload()], 1, CancellationToken.None);
        Assert.Equal(now, Assert.Single(target.Units).IngestedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheProcessorCheckpointsDeliveryManifestsAfterItsSourceWalBecomesDurablyEmpty(bool replay)
    {
        var settings = Settings();
        settings.DatabaseBatchCommitSize = 2;
        var options = Options.Create(settings);
        var items = new[] { Payload(), Payload() };
        if (replay)
        {
            await using var seed = new WriteAheadLogger(options);
            foreach (var item in items)
            {
                await seed.AppendAsync(item, CancellationToken.None);
            }
        }
        await using var wal = new WriteAheadLogger(options);
        var broker = new TelemetryChannelBroker(options);
        var target = new RecordingWriter(_source, _directory);
        var journaled = new JournaledTelemetryBatchWriter(target, new TelemetryDeliveryJournal(options), wal, options);
        using var processor = new TelemetryBatchProcessor(broker, wal, journaled, Substitute.For<IHostApplicationLifetime>(),
            options, NullLogger<TelemetryBatchProcessor>.Instance);
        if (!replay)
        {
            foreach (var item in items)
            {
                await wal.AppendAsync(item, CancellationToken.None);
                await broker.PublishAsync(item, CancellationToken.None);
            }
        }
        await processor.StartAsync(CancellationToken.None);
        await target.FirstDelivery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (replay ? wal.HasSealedSegment : wal.UncommittedRecordCount != 0)
        {
            await Task.Delay(10, deadline.Token);
        }
        await processor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "delivery")));
        Assert.Equal(items.Select(item => item.EventId), Assert.Single(target.Units).Items.Select(item => item.EventId));
        Assert.Equal(0, wal.RetainedWalBytes);
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
        var pending = Unit();
        List<string> trace = [];
        var journal = new TelemetryDeliveryJournal(settings,
            (path, mode) =>
            {
                Assert.Equal(pending.Id.ToString("N") + (mode == FileMode.CreateNew ? ".tmp" : ".committed"), Path.GetFileName(path));
                return new ProbeFileStream(path, mode, trace);
            },
            path => trace.Add("sync:" + Path.GetFileName(path)));
        Assert.Equal(new[] { "sync:" + Path.GetFileName(_directory) }, trace);
        trace.Clear();
        var unit = journal.Prepare(pending);
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
        Assert.Contains("DeliveryJournalMaxBytes", Assert.Throws<InvalidDataException>(() => limited.Load().ToArray()).Message);
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
            Assert.Contains("EnableAsyncInsert=false", Assert.Throws<InvalidOperationException>(() => new JournaledTelemetryBatchWriter(
                new RecordingWriter(_source, _directory), journal, wal, options)).Message);
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
        Assert.Contains("checksum does not match", Assert.Throws<InvalidDataException>(() => journal.Load().ToArray()).Message);
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
        Assert.Contains("manifest is empty", Assert.Throws<InvalidDataException>(() => journal.Load().ToArray()).Message);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    public void InvalidJournalCapacitiesFailBeforeCreatingTheMetadataDirectory(bool records, int value)
    {
        var settings = Settings();
        if (records)
        {
            settings.DeliveryJournalMaxRecords = value;
        }
        else
        {
            settings.DeliveryJournalMaxBytes = value;
        }
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TelemetryDeliveryJournal(Options.Create(settings)));
        Assert.Equal(records ? "_maxRecords" : "_maxBytes", error.ParamName);
        Assert.False(Directory.Exists(Path.Combine(_directory, "delivery")));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("items")]
    public void PersistedManifestsRequireTheirSchemaAndMembershipProperties(string property)
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        var unit = journal.Prepare(Unit());
        string path = Path.Combine(_directory, "delivery", unit.Id.ToString("N") + ".json");
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path).AsSpan(65))!.AsObject();
        Assert.True(document.Remove(property));
        WriteSignedManifest(path, document.ToJsonString());
        System.Text.Json.JsonException error = Assert.Throws<System.Text.Json.JsonException>(() => journal.Load().ToArray());
        Assert.Contains(property, error.Message);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AChecksummedManifestIsStillValidatedBeforeRecoveryCanPublishIt()
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        var unit = journal.Prepare(Unit());
        string path = Path.Combine(_directory, "delivery", unit.Id.ToString("N") + ".json");
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path).AsSpan(65))!.AsObject();
        document["schema"] = "invalid";
        WriteSignedManifest(path, document.ToJsonString());
        Assert.Contains("header is invalid", Assert.Throws<InvalidDataException>(() => journal.Load().ToArray()).Message);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AOneByteChecksummedBodyReachesStructuralValidationInsteadOfBeingCalledTruncated()
    {
        var journal = new TelemetryDeliveryJournal(Options.Create(Settings()));
        var unit = journal.Prepare(Unit());
        string path = Path.Combine(_directory, "delivery", unit.Id.ToString("N") + ".json");
        WriteSignedManifest(path, "0");
        Assert.Equal(66, new FileInfo(path).Length);
        Assert.Throws<System.Text.Json.JsonException>(() => journal.Load().ToArray());
    }

    [Fact]
    public void JournalBytesIncludeEveryChecksumHeaderAndAllowAnExactFullCapacity()
    {
        var settings = Settings();
        var unit = Unit();
        var second = new TelemetryDeliveryUnit
        {
            SourceId = unit.SourceId,
            Schema = unit.Schema,
            IngestedAt = unit.IngestedAt,
            Items = [Payload()]
        };
        second.Items[0].Timestamp = unit.Items[0].Timestamp;
        int length = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(unit,
            TelemetryAotContext.Default.TelemetryDeliveryUnit).Length + 65;
        settings.DeliveryJournalMaxBytes = length * 2;
        var journal = new TelemetryDeliveryJournal(Options.Create(settings));
        journal.Prepare(unit);
        journal.Prepare(second);
        Assert.Equal(length * 2L, Directory.GetFiles(Path.Combine(_directory, "delivery")).Sum(path => new FileInfo(path).Length));
        Assert.Contains("no capacity for another durable manifest", Assert.Throws<InvalidDataException>(() => journal.Prepare(Unit())).Message);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_directory, "delivery"), "*.json").Length);
    }

    [Fact]
    public void AManifestMayUseItsEncodedByteBudgetWithoutNeedingTheJsonWritersEntireScratchReservation()
    {
        var settings = Settings();
        var unit = Unit();
        unit.Items[0].MetadataJson = new string('x', 3000);
        int length = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(unit,
            TelemetryAotContext.Default.TelemetryDeliveryUnit).Length + 65;
        settings.DeliveryJournalMaxBytes = length;
        var journal = new TelemetryDeliveryJournal(Options.Create(settings));
        Assert.Equal(unit.Id, journal.Prepare(unit).Id);
        Assert.Equal(length, new FileInfo(Assert.Single(Directory.GetFiles(Path.Combine(_directory, "delivery")))).Length);
    }

    [Fact]
    public void EncodingAndRecoveryAllowAUnitInsideTheConfiguredExpansionAllowance()
    {
        var settings = Settings();
        settings.DatabaseBatchByteCapacity = 256;
        settings.DeliveryJournalMaxBytes = 16_384;
        var unit = Unit();
        unit.Items[0].MetadataJson = string.Empty;
        int overhead = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(unit,
            TelemetryAotContext.Default.TelemetryDeliveryUnit).Length;
        unit.Items[0].MetadataJson = new string('x', 4500 - overhead);
        var journal = new TelemetryDeliveryJournal(Options.Create(settings));

        journal.Prepare(unit);

        Assert.Equal(4565, new FileInfo(Assert.Single(Directory.GetFiles(Path.Combine(_directory, "delivery")))).Length);
        Assert.Equal(unit.Id, Assert.Single(journal.Load()).Id);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void EncodingIsBoundedByRemainingCapacityBeforeAnotherManifestCanBePublished(int remainingBodyBytes)
    {
        var settings = Settings();
        var unit = Unit();
        int length = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(unit,
            TelemetryAotContext.Default.TelemetryDeliveryUnit).Length + 65;
        settings.DeliveryJournalMaxBytes = length + 65 + remainingBodyBytes;
        var journal = new TelemetryDeliveryJournal(Options.Create(settings));
        journal.Prepare(unit);

        var failure = Assert.Throws<InvalidDataException>(() => journal.Prepare(Unit()));

        Assert.Contains("Serialized WAL record exceeds WalMaxRecordBytes", failure.Message);
        Assert.Equal(unit.Id.ToString("N") + ".json",
            Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(_directory, "delivery")))));
        Assert.Equal(unit.Id, Assert.Single(new TelemetryDeliveryJournal(Options.Create(settings)).Load()).Id);
    }

    [Fact]
    public void RecoveryChecksThePerUnitLimitBeforeReadingOrAllocatingTheManifest()
    {
        var settings = Settings();
        settings.DatabaseBatchByteCapacity = 256;
        settings.DeliveryJournalMaxBytes = 16_384;
        var original = new TelemetryDeliveryJournal(Options.Create(settings));
        var unit = original.Prepare(Unit());
        string path = Path.Combine(_directory, "delivery", unit.Id.ToString("N") + ".json");
        using (var file = new FileStream(path, FileMode.Open))
        {
            file.SetLength(256 * 4 + 4096 + 1);
        }
        bool read = false;
        var limited = new TelemetryDeliveryJournal(Options.Create(settings),
            (file, mode) => new FileStream(file, mode, FileAccess.Write, FileShare.None), _ => { }, file =>
            {
                read = true;
                return File.ReadAllBytes(file);
            });
        Assert.Contains("recovery byte limit", Assert.Throws<InvalidDataException>(() => limited.Load().ToArray()).Message);
        Assert.False(read);
    }

    private static void WriteSignedManifest(string path, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] header = Encoding.ASCII.GetBytes(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)) + "\n");
        File.WriteAllBytes(path, header.Concat(body).ToArray());
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
        Assert.Contains("no capacity for another durable manifest", Assert.Throws<InvalidDataException>(() => journal.Prepare(Unit())).Message);
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
        public TaskCompletionSource FirstDelivery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<TelemetryDeliveryUnit> Units { get; } = [];
        public string SchemaFingerprint => new('A', 64);
        public Task<Guid> GetSourceIdAsync(CancellationToken cancellationToken) => Task.FromResult(source);

        public Task WriteDeliveryAsync(TelemetryDeliveryUnit unit, CancellationToken cancellationToken)
        {
            Assert.True(File.Exists(Path.Combine(directory, "delivery", unit.Id.ToString("N") + ".json")));
            Units.Add(unit);
            FirstDelivery.TrySetResult();
            return Fail ? Task.FromException(new IOException("ambiguous insert")) : Task.CompletedTask;
        }

    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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

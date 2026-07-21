using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using NSubstitute;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public class WalRecoveryIntegrationTests : IAsyncLifetime
{
    private static readonly string ConnectionString = ClickHouseTestSupport.ConnectionString;

    private string _walDir = string.Empty;

    public Task InitializeAsync()
    {
        _walDir = Path.Combine(Path.GetTempPath(), $"nealytics_wal_recovery_{Guid.NewGuid():N}");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, recursive: true);
        }
        return Task.CompletedTask;
    }

    private IOptions<TelemetryEngineOptions> BuildOptions()
    {
        TelemetryEngineOptions options = new TelemetryEngineOptions
        {
            ClickHouseConnectionString = ConnectionString,
            WriteAheadLogDirectory = _walDir,
            DatabaseBatchCommitSize = 100,
            ForceFlushIntervalSeconds = 1,
            MemoryChannelCapacity = 10_000,
            MaxInsertRetries = 3
        };
        IOptions<TelemetryEngineOptions> wrapped = Substitute.For<IOptions<TelemetryEngineOptions>>();
        wrapped.Value.Returns(options);
        return wrapped;
    }

    [Fact]
    public async Task Recovery_ReplaysUncommittedWalEntries_IntoClickHouse_AndDeletesSealedSegment()
    {
        string projectId = $"p-recovery-{Guid.NewGuid():N}";
        IOptions<TelemetryEngineOptions> options = BuildOptions();

        // ── Simulate a crashed process that left 25 uncommitted events in the WAL ──
        await using (WriteAheadLogger crashedWal = new WriteAheadLogger(options))
        {
            for (int i = 0; i < 25; i++)
            {
                await crashedWal.AppendAsync(new GlobalTelemetryPayload
                {
                    ProjectId = projectId,
                    TenantId = "t-recovery",
                    SessionId = "s-recovery",
                    EventType = $"recovered_{i}",
                    MetadataJson = "{}"
                }, CancellationToken.None);
            }
        }

        // ── A fresh process boots: WAL seals the prior log, batch processor replays it ──
        await using WriteAheadLogger wal = new WriteAheadLogger(options);
        wal.HasSealedSegment.Should().BeTrue("the crashed process's WAL must be sealed for recovery");

        await using ClickHouseConnectionFactory factory = new ClickHouseConnectionFactory(options);
        TelemetryChannelBroker broker = new TelemetryChannelBroker(options);
        IHostApplicationLifetime lifetime = Substitute.For<IHostApplicationLifetime>();

        ClickHouseBatchWriter writer = new ClickHouseBatchWriter(factory, options);
        TelemetryBatchProcessor processor = new TelemetryBatchProcessor(
            broker, wal, writer, lifetime, options,
            NullLogger<TelemetryBatchProcessor>.Instance);

        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(3000);
        await processor.StopAsync(CancellationToken.None);

        // ── All recovered events must be durably in ClickHouse ──
        long count = await ClickHouseTestSupport.CountAsync(projectId);
        count.Should().Be(25, "every uncommitted WAL event must be replayed into ClickHouse on recovery");

        // ── The sealed segment must be gone once recovery committed ──
        wal.HasSealedSegment.Should().BeFalse("the sealed segment must be deleted after successful replay");
    }

    [Fact]
    public async Task SteadyStateCommit_TruncatesWal_OnceAllEventsPersisted()
    {
        string projectId = $"p-steady-{Guid.NewGuid():N}";
        IOptions<TelemetryEngineOptions> options = BuildOptions();

        await using WriteAheadLogger wal = new WriteAheadLogger(options);
        await using ClickHouseConnectionFactory factory = new ClickHouseConnectionFactory(options);
        TelemetryChannelBroker broker = new TelemetryChannelBroker(options);
        IHostApplicationLifetime lifetime = Substitute.For<IHostApplicationLifetime>();

        ClickHouseBatchWriter writer = new ClickHouseBatchWriter(factory, options);
        TelemetryBatchProcessor processor = new TelemetryBatchProcessor(
            broker, wal, writer, lifetime, options,
            NullLogger<TelemetryBatchProcessor>.Instance);

        await processor.StartAsync(CancellationToken.None);

        for (int i = 0; i < 10; i++)
        {
            GlobalTelemetryPayload payload = new GlobalTelemetryPayload
            {
                ProjectId = projectId,
                TenantId = "t-steady",
                SessionId = "s-steady",
                EventType = $"steady_{i}",
                MetadataJson = "{}"
            };
            await wal.AppendAsync(payload, CancellationToken.None);
            await broker.PublishAsync(payload, CancellationToken.None);
        }

        await Task.Delay(3000);
        await processor.StopAsync(CancellationToken.None);

        long count = await ClickHouseTestSupport.CountAsync(projectId);
        count.Should().Be(10, "all published events should flush to ClickHouse");
        wal.UncommittedRecordCount.Should().Be(0,
            "the WAL must be acknowledged/truncated once every event is committed");
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Delivery;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Integration;

public sealed class ReplayDeliveryIntegrationTests
{
    [Fact]
    public async Task AnAmbiguousCommitRestartsWithTheSameUnitAcrossChangedReplayBatching()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-delivery-real-" + Guid.NewGuid().ToString("N"));
        await using TestProject project = TestProject.New("p-delivery");
        var options = Options.Create(new TelemetryEngineOptions
        {
            WriteAheadLogDirectory = directory,
            ClickHouseConnectionString = ClickHouseTestSupport.ConnectionString,
            EnableAsyncInsert = false
        });
        await ClickHouseTestSupport.ExecuteAsync(
            "ALTER TABLE nealytics_core.global_events MODIFY SETTING non_replicated_deduplication_window = 100000");
        await using ClickHouseConnectionFactory factory = new(options);
        var dimensions = new DimensionRegistry(options.Value);
        var target = new ClickHouseBatchWriter(factory, dimensions, new MeasureRegistry(options.Value, dimensions),
            options, NullLogger<ClickHouseBatchWriter>.Instance);
        var ambiguous = new CommitThenDisconnect(target);
        GlobalTelemetryPayload[] items = Enumerable.Range(0, 4).Select(index => new GlobalTelemetryPayload
        {
            ProjectId = project.Id, TenantId = "tenant", EventType = "event", SessionId = "s",
            Timestamp = DateTime.UtcNow.AddDays(index % 2 == 0 ? -32 : 0)
        }).ToArray();
        try
        {
            await using (WriteAheadLogger wal = new(options))
            {
                foreach (var item in items)
                {
                    await wal.AppendAsync(item, CancellationToken.None);
                }
                var writer = new JournaledTelemetryBatchWriter(ambiguous, new TelemetryDeliveryJournal(options), wal, options);
                await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(items, 4, CancellationToken.None));
            }
            Assert.Equal(4, await ClickHouseTestSupport.CountAsync(project.Id));
            DateTimeOffset frozen = ambiguous.Attempt!.IngestedAt;
            await using (WriteAheadLogger wal = new(options))
            {
                var recovered = await wal.ReplayUncommittedAsync();
                var writer = new JournaledTelemetryBatchWriter(target, new TelemetryDeliveryJournal(options), wal, options);
                await writer.WriteAsync([recovered[2]], 1, CancellationToken.None);
                await writer.WriteAsync(recovered, 4, CancellationToken.None);
                Assert.Equal(4, await ClickHouseTestSupport.CountAsync(project.Id));
                await using var connection = new ClickHouseConnection(ClickHouseTestSupport.ConnectionString);
                await connection.OpenAsync();
                await using var query = connection.CreateCommand(
                    $"SELECT uniqExact(ingested_at), min(toUnixTimestamp64Milli(ingested_at)) FROM nealytics_core.global_events WHERE project_id = '{project.Id}'");
                await using var reader = await query.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal((ulong)1, reader.GetFieldValue<ulong>(0));
                Assert.Equal(frozen.ToUnixTimeMilliseconds(), reader.GetInt64(1));
                await wal.DeleteSealedSegmentAsync();
                await writer.CheckpointAsync();
                Assert.Empty(Directory.GetFiles(Path.Combine(directory, "delivery")));
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PartialMaterializedViewFailureCanRetryWithoutDuplicatingSuccessfulTargets()
    {
        string database = "delivery_partial_" + Guid.NewGuid().ToString("N");
        async Task Execute(string sql)
        {
            await using var connection = new ClickHouseConnection(ClickHouseTestSupport.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        async Task<ulong> Sum(string table)
        {
            await using var connection = new ClickHouseConnection(ClickHouseTestSupport.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand($"SELECT sum(n) FROM {database}.{table}");
            return Convert.ToUInt64(await command.ExecuteScalarAsync());
        }
        try
        {
            await Execute($"CREATE DATABASE {database}");
            foreach (string table in new[] { "source", "first_target", "last_target" })
            {
                await Execute($"CREATE TABLE {database}.{table} (id UInt64, n UInt64) ENGINE=MergeTree ORDER BY id SETTINGS non_replicated_deduplication_window=100000");
            }
            await Execute($"CREATE MATERIALIZED VIEW {database}.first_mv TO {database}.first_target AS SELECT id,n FROM {database}.source");
            await Execute($"CREATE MATERIALIZED VIEW {database}.last_mv TO {database}.last_target AS SELECT id,toUInt64(intDiv(n,id-1)) AS n FROM {database}.source");
            string insert = $"INSERT INTO {database}.source SETTINGS insert_deduplication_token='fixed-unit', insert_deduplicate=1, deduplicate_blocks_in_dependent_materialized_views=1 VALUES (1,7)";
            await Assert.ThrowsAnyAsync<Exception>(() => Execute(insert));
            Assert.Equal((ulong)7, await Sum("source"));
            await Execute($"DROP VIEW {database}.last_mv");
            await Execute($"CREATE MATERIALIZED VIEW {database}.last_mv TO {database}.last_target AS SELECT id,n FROM {database}.source");
            await Execute(insert);
            await Execute(insert);
            Assert.Equal((ulong)7, await Sum("source"));
            Assert.Equal((ulong)7, await Sum("first_target"));
            Assert.Equal((ulong)7, await Sum("last_target"));
        }
        finally
        {
            await Execute($"DROP DATABASE IF EXISTS {database} SYNC");
        }
    }

    [Fact]
    public async Task TokenEvictionIsExplicitlyOutsideTheDeduplicationGuarantee()
    {
        string database = "delivery_window_" + Guid.NewGuid().ToString("N");
        try
        {
            await ClickHouseTestSupport.ExecuteAsync($"CREATE DATABASE {database}");
            await ClickHouseTestSupport.ExecuteAsync($"CREATE TABLE {database}.events (id UInt64) ENGINE=MergeTree ORDER BY id SETTINGS non_replicated_deduplication_window=1");
            foreach (string token in new[] { "original", "replacement", "original" })
            {
                await ClickHouseTestSupport.ExecuteAsync($"INSERT INTO {database}.events SETTINGS insert_deduplication_token='{token}' VALUES (1)");
            }
            await using var connection = new ClickHouseConnection(ClickHouseTestSupport.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand($"SELECT count() FROM {database}.events");
            Assert.Equal((ulong)3, await command.ExecuteScalarAsync());
        }
        finally
        {
            await ClickHouseTestSupport.ExecuteAsync($"DROP DATABASE IF EXISTS {database} SYNC");
        }
    }

    private sealed class CommitThenDisconnect(IDeliveryBatchWriter inner) : IDeliveryBatchWriter
    {
        public TelemetryDeliveryUnit? Attempt;
        public string SchemaFingerprint => inner.SchemaFingerprint;
        public Task<Guid> GetSourceIdAsync(CancellationToken cancellationToken) => inner.GetSourceIdAsync(cancellationToken);

        public async Task WriteDeliveryAsync(TelemetryDeliveryUnit unit, CancellationToken cancellationToken)
        {
            Attempt = unit;
            await inner.WriteDeliveryAsync(unit, cancellationToken);
            throw new IOException("commit response lost");
        }
    }
}

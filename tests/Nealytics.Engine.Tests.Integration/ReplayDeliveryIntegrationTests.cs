using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Delivery;
using Octonica.ClickHouseClient;
using Testcontainers.ClickHouse;

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
                await using (var reader = await query.ExecuteReaderAsync())
                {
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal((ulong)1, reader.GetFieldValue<ulong>(0));
                    Assert.Equal(frozen.ToUnixTimeMilliseconds(), reader.GetInt64(1));
                }
                await using (var flush = connection.CreateCommand("SYSTEM FLUSH LOGS"))
                {
                    await flush.ExecuteNonQueryAsync();
                }
                string token = ambiguous.Attempt.Id.ToString("N");
                await using (var logged = connection.CreateCommand(
                    "SELECT query FROM system.query_log WHERE type='QueryFinish' AND query_kind='Insert' "
                    + $"AND position(replaceAll(query, ' ', ''), 'insert_deduplication_token=''{token}''') > 0"))
                {
                    await using var entries = await logged.ExecuteReaderAsync();
                    int attempts = 0;
                    while (await entries.ReadAsync())
                    {
                        string text = entries.GetString(0).Replace(" ", "");
                        Assert.Contains("async_insert=0", text);
                        Assert.Contains("insert_deduplicate=1", text);
                        Assert.Contains("deduplicate_blocks_in_dependent_materialized_views=1", text);
                        Assert.Contains("min_insert_block_size_rows=0", text);
                        Assert.Contains("min_insert_block_size_bytes=0", text);
                        attempts++;
                    }
                    Assert.Equal(2, attempts);
                }
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

    [Fact]
    public async Task SourceIdentityRequiresTheSupportedEngineAndAnActiveDeduplicationLog()
    {
        await using ClickHouseContainer server = new ClickHouseBuilder(
            "clickhouse/clickhouse-server:25.3.14.14@sha256:b627d7a9bc0e0c1bac26cdbe9d2fc6316faa29c5d8a174f28f5abd57d0fa6ba2")
            .WithDatabase("nealytics_core").WithUsername("delivery").WithPassword("delivery-test-only")
            .WithLabel("nealytics.scope", "delivery-identity-qualification")
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig!.Memory = 2147483648;
                parameters.HostConfig.NanoCPUs = 2000000000;
            }).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await server.StartAsync(timeout.Token);
        var connectionString = new ClickHouseConnectionStringBuilder
        {
            Host = server.Hostname, Port = server.GetMappedPublicPort(9000), Database = "nealytics_core",
            User = "delivery", Password = "delivery-test-only"
        }.ToString();
        var options = Options.Create(new TelemetryEngineOptions { ClickHouseConnectionString = connectionString });
        await using var connection = new ClickHouseConnection(connectionString);
        await connection.OpenAsync(timeout.Token);
        async Task Execute(string sql)
        {
            await using var command = connection.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
        await Execute("CREATE TABLE nealytics_core.global_events (event_id UUID) ENGINE=ReplacingMergeTree ORDER BY event_id");
        await using var factory = new ClickHouseConnectionFactory(options);
        var dimensions = new DimensionRegistry(options.Value);
        var writer = new ClickHouseBatchWriter(factory, dimensions, new MeasureRegistry(options.Value, dimensions),
            options, NullLogger<ClickHouseBatchWriter>.Instance);
        async Task AssertUnsupportedSource()
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.GetSourceIdAsync(timeout.Token));
            Assert.Equal("Replay-safe delivery requires an Atomic ReplacingMergeTree source and a deduplication window of at least 100000 blocks.", error.Message);
        }
        await AssertUnsupportedSource();
        await Execute("ALTER TABLE nealytics_core.global_events MODIFY SETTING non_replicated_deduplication_window = 100000");
        Assert.NotEqual(Guid.Empty, await writer.GetSourceIdAsync(timeout.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => writer.WriteAsync(
            [new GlobalTelemetryPayload { ProjectId = "p", TenantId = "t", EventType = "e" }], 1, timeout.Token));
        Assert.NotEqual(Guid.Empty, await writer.GetSourceIdAsync(timeout.Token));
        await Execute("DROP DATABASE nealytics_core SYNC");
        await Execute("CREATE DATABASE nealytics_core");
        await Execute("CREATE TABLE nealytics_core.global_events (event_id UUID) ENGINE=MergeTree ORDER BY event_id SETTINGS non_replicated_deduplication_window=100000");
        await AssertUnsupportedSource();
        var ordinary = await server.ExecScriptAsync(
            "SET allow_deprecated_database_ordinary=1; DROP DATABASE nealytics_core SYNC; "
            + "CREATE DATABASE nealytics_core ENGINE=Ordinary; "
            + "CREATE TABLE nealytics_core.global_events (event_id UUID) ENGINE=ReplacingMergeTree "
            + "ORDER BY event_id SETTINGS non_replicated_deduplication_window=100000;", timeout.Token);
        Assert.Equal(0, ordinary.ExitCode);
        await AssertUnsupportedSource();
    }

    [Fact]
    public async Task PendingDeliveryPreventsNewRollupBackfillAndReadinessUntilItIsResolved()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-delivery-rollup-" + Guid.NewGuid().ToString("N"));
        string name = "delivery_" + Guid.NewGuid().ToString("N");
        string delivery = Path.Combine(directory, "delivery");
        Directory.CreateDirectory(delivery);
        string manifest = Path.Combine(delivery, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(manifest, "pending");
        var options = Options.Create(new TelemetryEngineOptions
        {
            WriteAheadLogDirectory = directory,
            ClickHouseConnectionString = ClickHouseTestSupport.ConnectionString,
            Rollups = [new RollupOptions { Name = name }]
        });
        await using var factory = new ClickHouseConnectionFactory(options);
        var dimensions = new DimensionRegistry(options.Value);
        var measures = new MeasureRegistry(options.Value, dimensions);
        var registry = new RollupRegistry(options.Value, dimensions, measures);
        var migrator = new ClickHouseSchemaMigrator(factory, dimensions, measures, registry, options,
            NullLogger<ClickHouseSchemaMigrator>.Instance);
        var rollup = Assert.Single(registry.Declared);
        try
        {
            await migrator.StartAsync(CancellationToken.None);
            Assert.False(registry.IsRoutable(rollup));
            Assert.Contains("pending delivery", registry.UnroutableReason(rollup));
            await using var connection = new ClickHouseConnection(ClickHouseTestSupport.ConnectionString);
            await connection.OpenAsync();
            await using (var count = connection.CreateCommand(
                $"SELECT count() FROM system.tables WHERE database='nealytics_core' AND name='rollup_{name}'"))
            {
                Assert.Equal((ulong)0, await count.ExecuteScalarAsync());
            }
            File.WriteAllText(Path.ChangeExtension(manifest, ".committed"), string.Empty);
            await migrator.StartAsync(CancellationToken.None);
            Assert.True(registry.IsRoutable(rollup));
        }
        finally
        {
            await ClickHouseTestSupport.ExecuteAsync($"DROP VIEW IF EXISTS nealytics_core.rollup_{name}_mv");
            await ClickHouseTestSupport.ExecuteAsync($"DROP TABLE IF EXISTS nealytics_core.rollup_{name} SYNC");
            Directory.Delete(directory, true);
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

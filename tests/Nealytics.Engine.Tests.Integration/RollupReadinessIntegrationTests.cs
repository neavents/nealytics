using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Rollups;
using Octonica.ClickHouseClient.Exceptions;
using Testcontainers.ClickHouse;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class RollupReadinessIntegrationTests : IAsyncLifetime
{
    private readonly string _name = "ready_" + Guid.NewGuid().ToString("N")[..14];
    private readonly TestProject _project = TestProject.New("p-readiness");
    private readonly List<ClickHouseConnectionFactory> _factories = [];
    private readonly HashSet<Guid> _ownedTables = [];
    private static readonly DateTime Early = DateTime.UtcNow.Date.AddDays(-40);
    private static readonly DateTime Late = DateTime.UtcNow.Date.AddDays(-5);

    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FreshServerCreatesTheReadinessSchemaBeforePublishingAnyRollup()
    {
        await using ClickHouseContainer server = new ClickHouseBuilder(
            "clickhouse/clickhouse-server:25.3.14.14@sha256:b627d7a9bc0e0c1bac26cdbe9d2fc6316faa29c5d8a174f28f5abd57d0fa6ba2")
            .WithDatabase("nealytics_core")
            .WithUsername("readiness")
            .WithPassword("readiness-test-only")
            .WithLabel("nealytics.scope", "a5-cold-start")
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig!.Memory = 2_147_483_648;
                parameters.HostConfig.NanoCPUs = 2_000_000_000;
            })
            .Build();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await server.StartAsync(timeout.Token);
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "clickhouse-init.sql")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var initialization = await server.ExecScriptAsync(
            await File.ReadAllTextAsync(Path.Combine(directory.FullName, "clickhouse-init.sql"), timeout.Token), timeout.Token);
        Assert.Equal(0, initialization.ExitCode);
        Octonica.ClickHouseClient.ClickHouseConnectionStringBuilder connection = new()
        {
            Host = server.Hostname,
            Port = server.GetMappedPublicPort(9000),
            Database = "nealytics_core",
            User = "readiness",
            Password = "readiness-test-only"
        };
        var current = Build(connectionString: connection.ToString());
        await current.Migrator.StartAsync(timeout.Token);
        Assert.Empty(current.Logger.Errors);
        Assert.Single(current.Registry.Routable);
        Assert.Contains(current.Logger.Entries, entry => entry.Id == 8120 && entry.Message.Contains(_name));
        await using PooledClickHouseConnection pooled = await current.Factory.AcquireAsync(timeout.Token);
        await using var schema = pooled.Connection.CreateCommand(
            "SELECT name, type FROM system.columns WHERE database = 'nealytics_core' AND table = 'rollup_readiness' ORDER BY position");
        await using var reader = await schema.ExecuteReaderAsync(timeout.Token);
        List<(string, string)> columns = [];
        while (await reader.ReadAsync(timeout.Token))
        {
            columns.Add((reader.GetString(0), reader.GetString(1)));
        }
        Assert.Equal(new (string, string)[]
        {
            ("source_id", "UUID"), ("table_id", "UUID"), ("view_id", "UUID"),
            ("declared_definition", "FixedString(64)"), ("stored_definition", "FixedString(64)"),
            ("backfilled", "UInt8"), ("completed_at", "DateTime64(3, 'UTC')")
        }, columns);
    }

    [Fact]
    public async Task HistoricalCompletionSurvivesFreshStartupWithoutReinserting()
    {
        await SeedAsync();
        var first = Build();
        Assert.Empty(first.Registry.Routable);
        await first.Migrator.StartAsync(CancellationToken.None);
        Assert.Empty(first.Logger.Errors);
        Assert.Contains(first.Logger.Entries, entry => entry.Id == 8120 && entry.Message.Contains(_name));
        Assert.True(first.Registry.IsRoutable(first.Registry.Declared[0]));
        Assert.Equal(2, await StoredEventsAsync());
        await AssertResultAsync(first.Registry, first.Factory, "rollup:" + _name, 2);

        var restarted = Build();
        await restarted.Migrator.StartAsync(CancellationToken.None);
        Assert.Empty(restarted.Logger.Errors);
        Assert.True(restarted.Registry.IsRoutable(restarted.Registry.Declared[0]));
        Assert.Equal(2, await StoredEventsAsync());
        await using PooledClickHouseConnection pooled = await first.Factory.AcquireAsync(CancellationToken.None);
        ClickHouseRollupReadiness readiness = new(pooled);
        RollupStorageIdentity identity = await CaptureAsync(readiness, first.Registry.Declared[0]);
        Assert.True(await readiness.IsCompleteAsync(identity, true, CancellationToken.None));
    }

    [Fact]
    public async Task ForwardOnlyCompletionCannotProveHistoricalCoverage()
    {
        await SeedAsync();
        var forward = Build(backfill: false);
        await forward.Migrator.StartAsync(CancellationToken.None);
        Assert.Empty(forward.Logger.Errors);
        Assert.Contains(forward.Logger.Entries, entry => entry.Id == 8122 && entry.Message.Contains(_name));
        Assert.True(forward.Registry.IsRoutable(forward.Registry.Declared[0]));
        Assert.Equal(0, await StoredEventsAsync());

        var historical = Build();
        await historical.Migrator.StartAsync(CancellationToken.None);
        Assert.False(historical.Registry.IsRoutable(historical.Registry.Declared[0]));
        await AssertResultAsync(historical.Registry, historical.Factory, "raw", 2);
        var forwardRestart = Build(backfill: false);
        await forwardRestart.Migrator.StartAsync(CancellationToken.None);
        Assert.True(forwardRestart.Registry.IsRoutable(forwardRestart.Registry.Declared[0]));
        Assert.Equal(0, await StoredEventsAsync());
    }

    [Fact]
    public async Task AFailedBackfillPrefixStaysRawAfterRepeatedStartupWithoutDuplicatingIt()
    {
        await SeedAsync();
        var initial = Build();
        Rollup rollup = initial.Registry.Declared[0];
        await CreateStorageAsync(rollup);
        await ClickHouseTestSupport.ExecuteAsync(
            $"ALTER TABLE nealytics_core.{rollup.TableName} ADD CONSTRAINT first_partition "
            + $"CHECK bucket < toDateTime('{Late:yyyy-MM}-01 00:00:00', 'UTC')");
        await ClickHouseTestSupport.ExecuteAsync(RollupRegistry.BuildBackfillSql(rollup, Early.ToString("yyyyMM", CultureInfo.InvariantCulture)));
        await Assert.ThrowsAsync<ClickHouseServerException>(() => ClickHouseTestSupport.ExecuteAsync(
            RollupRegistry.BuildBackfillSql(rollup, Late.ToString("yyyyMM", CultureInfo.InvariantCulture))));
        Assert.Equal(1, await StoredEventsAsync());

        for (int restart = 0; restart < 2; restart++)
        {
            var current = Build();
            await current.Migrator.StartAsync(CancellationToken.None);
            Assert.False(current.Registry.IsRoutable(current.Registry.Declared[0]));
            await AssertResultAsync(current.Registry, current.Factory, "raw", 2);
            Assert.Equal(1, await StoredEventsAsync());
        }
    }

    [Fact]
    public async Task ATableWithoutItsViewIsNotBackfilledAdditively()
    {
        await SeedAsync();
        var current = Build();
        Rollup rollup = current.Registry.Declared[0];
        await CreateStorageAsync(rollup, view: false);
        await ClickHouseTestSupport.ExecuteAsync(RollupRegistry.BuildBackfillSql(rollup, Early.ToString("yyyyMM", CultureInfo.InvariantCulture)));
        await current.Migrator.StartAsync(CancellationToken.None);
        Assert.False(current.Registry.IsRoutable(rollup));
        Assert.Equal(1, await StoredEventsAsync());
        await using PooledClickHouseConnection pooled = await current.Factory.AcquireAsync(CancellationToken.None);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ClickHouseRollupReadiness(pooled).CaptureAsync(rollup, CancellationToken.None));
        Assert.Equal("Rollup '" + rollup.Name + "' is missing its table or view.", error.Message);
        Assert.Contains(current.Logger.Entries, entry => entry.Id == 8124 && entry.Message.Contains("completion has not been verified"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingTheViewOrBothPhysicalObjectsInvalidatesCompletion(bool replaceTable)
    {
        await SeedAsync();
        var initial = Build();
        await initial.Migrator.StartAsync(CancellationToken.None);
        Rollup rollup = initial.Registry.Declared[0];
        await using PooledClickHouseConnection pooled = await initial.Factory.AcquireAsync(CancellationToken.None);
        ClickHouseRollupReadiness readiness = new(pooled);
        RollupStorageIdentity original = await CaptureAsync(readiness, rollup);
        await ClickHouseTestSupport.ExecuteAsync($"DROP VIEW nealytics_core.{rollup.ViewName}");
        if (replaceTable)
        {
            await ClickHouseTestSupport.ExecuteAsync($"DROP TABLE IF EXISTS nealytics_core.rollup_{_name} SYNC");
        }
        await CreateStorageAsync(rollup);
        RollupStorageIdentity replacement = await CaptureAsync(readiness, rollup);
        Assert.NotEqual(original.ViewId, replacement.ViewId);
        Assert.Equal(!replaceTable, original.TableId == replacement.TableId);
        Assert.False(await readiness.IsCompleteAsync(replacement, true, CancellationToken.None));
        var restarted = Build();
        await restarted.Migrator.StartAsync(CancellationToken.None);
        Assert.False(restarted.Registry.IsRoutable(rollup));
        await AssertResultAsync(restarted.Registry, restarted.Factory, "raw", 2);
        Assert.Equal(replaceTable ? 0 : 2, await StoredEventsAsync());
    }

    [Fact]
    public async Task ChangedStoredDefinitionCannotReuseAnOlderCompletion()
    {
        await SeedAsync();
        var initial = Build();
        await initial.Migrator.StartAsync(CancellationToken.None);
        await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.rollup_{_name} ADD COLUMN extra Nullable(String)");
        var restarted = Build();
        await restarted.Migrator.StartAsync(CancellationToken.None);
        Assert.False(restarted.Registry.IsRoutable(restarted.Registry.Declared[0]));
        Assert.Contains(restarted.Logger.Entries, entry => entry.Id == 8124 && entry.Message.Contains("completion has not been verified"));
        await AssertResultAsync(restarted.Registry, restarted.Factory, "raw", 2);
    }

    [Fact]
    public async Task ChangedEventCoverageCannotReuseAnOlderCompletion()
    {
        await SeedAsync();
        var initial = Build();
        await initial.Migrator.StartAsync(CancellationToken.None);
        var restarted = Build(eventTypes: "ready_e,other_e");
        await restarted.Migrator.StartAsync(CancellationToken.None);
        Assert.False(restarted.Registry.IsRoutable(restarted.Registry.Declared[0]));
        await AssertResultAsync(restarted.Registry, restarted.Factory, "raw", 2);
    }

    [Fact]
    public async Task ChangedDeclaredShapeKeepsItsSpecificDriftDiagnostic()
    {
        await SeedAsync();
        var initial = Build();
        await initial.Migrator.StartAsync(CancellationToken.None);
        var drifted = Build(dimensions: "object_id");
        await drifted.Migrator.StartAsync(CancellationToken.None);
        Assert.Empty(drifted.Registry.Routable);
        Assert.Equal("stored definition differs from the declared one", drifted.Registry.UnroutableReason(drifted.Registry.Declared[0]));
        Assert.Contains(drifted.Logger.Entries, entry => entry.Id == 8121 && entry.Message.Contains(_name));
        Assert.DoesNotContain(drifted.Logger.Entries, entry => entry.Id == 8124);
        Assert.Empty(drifted.Logger.Errors);
        await AssertResultAsync(drifted.Registry, drifted.Factory, "raw", 2);
    }

    [Fact]
    public async Task MissingStorageCannotProduceAnIdentityEvenWhenTheViewExists()
    {
        var current = Build();
        Rollup rollup = current.Registry.Declared[0];
        await CreateStorageAsync(rollup);
        await using PooledClickHouseConnection pooled = await current.Factory.AcquireAsync(CancellationToken.None);
        Rollup missing = rollup with { TableName = "missing_" + Guid.NewGuid().ToString("N") };
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ClickHouseRollupReadiness(pooled).CaptureAsync(missing, CancellationToken.None));
        Assert.Equal("Rollup '" + rollup.Name + "' is missing its table or view.", error.Message);
    }

    [Fact]
    public async Task NoRollupDeclarationRequiresNoReadinessSchemaPrivileges()
    {
        string user = "no_rollup_" + Guid.NewGuid().ToString("N");
        await ClickHouseTestSupport.ExecuteAsync($"CREATE USER {user} IDENTIFIED WITH no_password");
        try
        {
            await ClickHouseTestSupport.ExecuteAsync($"GRANT SELECT, ALTER TABLE ON nealytics_core.global_events TO {user}");
            await ClickHouseTestSupport.ExecuteAsync($"GRANT SELECT ON system.* TO {user}");
            Octonica.ClickHouseClient.ClickHouseConnectionStringBuilder connection = new(ClickHouseTestSupport.ConnectionString)
            {
                User = user,
                Password = string.Empty
            };
            var current = Build(connectionString: connection.ToString(), declareRollup: false);
            await current.Migrator.StartAsync(CancellationToken.None);
            Assert.Empty(current.Registry.Declared);
            Assert.Empty(current.Logger.Errors);
            Assert.Contains(current.Logger.Entries, entry => entry.Id == 8002);
        }
        finally
        {
            await ClickHouseTestSupport.ExecuteAsync($"DROP USER IF EXISTS {user}");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task EveryPartOfThePersistedIdentityMustMatch(int changed)
    {
        var current = Build();
        Rollup rollup = current.Registry.Declared[0];
        await CreateStorageAsync(rollup);
        await using PooledClickHouseConnection pooled = await current.Factory.AcquireAsync(CancellationToken.None);
        ClickHouseRollupReadiness readiness = new(pooled);
        await readiness.EnsureCreatedAsync(string.Empty, CancellationToken.None);
        RollupStorageIdentity identity = await CaptureAsync(readiness, rollup);
        Assert.False(await readiness.IsCompleteAsync(identity, true, CancellationToken.None));
        await readiness.CompleteAsync(identity, true, CancellationToken.None);
        Assert.True(await readiness.IsCompleteAsync(identity, true, CancellationToken.None));
        Assert.True(await readiness.IsCompleteAsync(identity, false, CancellationToken.None));
        RollupStorageIdentity different = new(
            changed == 0 ? Guid.NewGuid() : identity.SourceId,
            changed == 1 ? Guid.NewGuid() : identity.TableId,
            changed == 2 ? Guid.NewGuid() : identity.ViewId,
            changed == 3 ? new string('F', 64) : identity.DeclaredDefinition,
            changed == 4 ? new string('F', 64) : identity.StoredDefinition);
        Assert.False(await readiness.IsCompleteAsync(different, true, CancellationToken.None));
    }

    [Fact]
    public async Task CanceledReadinessOperationsCannotPublishACompletion()
    {
        var current = Build();
        Rollup rollup = current.Registry.Declared[0];
        await CreateStorageAsync(rollup);
        await using PooledClickHouseConnection pooled = await current.Factory.AcquireAsync(CancellationToken.None);
        ClickHouseRollupReadiness readiness = new(pooled);
        await readiness.EnsureCreatedAsync(string.Empty, CancellationToken.None);
        RollupStorageIdentity identity = await CaptureAsync(readiness, rollup);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness.EnsureCreatedAsync(string.Empty, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness.CaptureAsync(rollup, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness.CompleteAsync(identity, true, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness.IsCompleteAsync(identity, true, canceled.Token));
        Assert.False(await readiness.IsCompleteAsync(identity, true, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentCreatorsOnlyRouteAfterPersistedCompletionAndNeverRepeatBackfill()
    {
        await SeedAsync();
        var first = Build();
        var second = Build();
        await Task.WhenAll(first.Migrator.StartAsync(CancellationToken.None), second.Migrator.StartAsync(CancellationToken.None));
        Assert.True(first.Registry.Routable.Any() || second.Registry.Routable.Any());
        Assert.Equal(2, await StoredEventsAsync());
        await first.Migrator.StartAsync(CancellationToken.None);
        await second.Migrator.StartAsync(CancellationToken.None);
        Assert.Single(first.Registry.Routable);
        Assert.Single(second.Registry.Routable);
        Assert.Equal(2, await StoredEventsAsync());
    }

    [Fact]
    public async Task FailureToPersistCompletionKeepsCompletedAggregatesUnroutableAcrossRestart()
    {
        await SeedAsync();
        string user = "ready_" + Guid.NewGuid().ToString("N");
        await ClickHouseTestSupport.ExecuteAsync($"CREATE USER {user} IDENTIFIED WITH no_password");
        try
        {
            await ClickHouseTestSupport.ExecuteAsync($"GRANT SELECT, INSERT, ALTER TABLE, CREATE TABLE, CREATE VIEW ON nealytics_core.* TO {user}");
            await ClickHouseTestSupport.ExecuteAsync($"GRANT SELECT ON system.* TO {user}");
            await ClickHouseTestSupport.ExecuteAsync($"REVOKE INSERT ON {ClickHouseRollupReadiness.TableName} FROM {user}");
            Octonica.ClickHouseClient.ClickHouseConnectionStringBuilder connection = new(ClickHouseTestSupport.ConnectionString)
            {
                User = user,
                Password = string.Empty
            };
            ErrorLogger logger = new();
            var failed = Build(connectionString: connection.ToString(), logger: logger);
            await failed.Migrator.StartAsync(CancellationToken.None);
            Assert.False(failed.Registry.IsRoutable(failed.Registry.Declared[0]));
            Assert.Equal(2, await StoredEventsAsync());
            ClickHouseServerException error = Assert.IsType<ClickHouseServerException>(Assert.Single(logger.Errors));
            Assert.Equal(497, error.ServerErrorCode);
            var restarted = Build();
            await restarted.Migrator.StartAsync(CancellationToken.None);
            Assert.Empty(restarted.Registry.Routable);
            Assert.Equal(2, await StoredEventsAsync());
            await AssertResultAsync(restarted.Registry, restarted.Factory, "raw", 2);
        }
        finally
        {
            await ClickHouseTestSupport.ExecuteAsync($"DROP USER IF EXISTS {user}");
        }
    }

    private (ClickHouseSchemaMigrator Migrator, RollupRegistry Registry, ClickHouseConnectionFactory Factory, ErrorLogger Logger) Build(
        bool backfill = true,
        string eventTypes = "ready_e",
        string? connectionString = null,
        ErrorLogger? logger = null,
        string dimensions = "",
        bool declareRollup = true)
    {
        TelemetryEngineOptions options = new()
        {
            ClickHouseConnectionString = connectionString ?? ClickHouseTestSupport.ConnectionString,
            ConnectionPoolSize = 2,
            BackfillRollups = backfill,
            Rollups = declareRollup
                ? [new RollupOptions { Name = _name, Grain = "day", EventTypes = eventTypes, Dimensions = dimensions }]
                : []
        };
        ClickHouseConnectionFactory factory = new(Options.Create(options));
        _factories.Add(factory);
        DimensionRegistry dimensionRegistry = new(options);
        MeasureRegistry measures = new(options, dimensionRegistry);
        RollupRegistry registry = new(options, dimensionRegistry, measures);
        logger ??= new ErrorLogger();
        ClickHouseSchemaMigrator migrator = new(factory, dimensionRegistry, measures, registry, Options.Create(options), logger);
        return (migrator, registry, factory, logger);
    }

    private async Task<RollupStorageIdentity> CaptureAsync(ClickHouseRollupReadiness readiness, Rollup rollup)
    {
        RollupStorageIdentity identity = await readiness.CaptureAsync(rollup, CancellationToken.None);
        _ownedTables.Add(identity.TableId);
        return identity;
    }

    private static async Task CreateStorageAsync(Rollup rollup, bool view = true)
    {
        await ClickHouseTestSupport.ExecuteAsync(RollupRegistry.BuildTableDdl(rollup));
        if (view)
        {
            await ClickHouseTestSupport.ExecuteAsync(RollupRegistry.BuildViewDdl(rollup));
        }
    }

    private Task SeedAsync() => ClickHouseTestSupport.ExecuteAsync(
        "INSERT INTO nealytics_core.global_events (event_id, project_id, tenant_id, session_id, event_type, timestamp) VALUES "
        + $"(generateUUIDv4(), '{_project.Id}', 't-ready', 's1', 'ready_e', toDateTime64('{Early:yyyy-MM-dd} 12:00:00', 3, 'UTC')), "
        + $"(generateUUIDv4(), '{_project.Id}', 't-ready', 's2', 'ready_e', toDateTime64('{Late:yyyy-MM-dd} 12:00:00', 3, 'UTC'))");

    private async Task<long> StoredEventsAsync()
    {
        await using Octonica.ClickHouseClient.ClickHouseConnection connection = new(ClickHouseTestSupport.ConnectionString);
        await connection.OpenAsync();
        await using Octonica.ClickHouseClient.ClickHouseCommand command = connection.CreateCommand(
            $"SELECT countMerge(events) FROM nealytics_core.rollup_{_name} WHERE project_id = {{project:String}}");
        command.Parameters.Add(new Octonica.ClickHouseClient.ClickHouseParameter { ParameterName = "project", Value = _project.Id });
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task AssertResultAsync(RollupRegistry registry, ClickHouseConnectionFactory factory, string source, long count)
    {
        BreakdownRequest request = new()
        {
            ProjectId = _project.Id, TenantId = "t-ready", Metric = BreakdownMetric.Events, MetricWire = "events",
            GroupByColumn = "event_type", EventType = "ready_e", TrafficClass = "normal", Filters = [],
            From = Early.AddDays(-1), To = Late.AddDays(1), Limit = 10, Order = BreakdownOrder.ValueDescending
        };
        BreakdownResponse response = await new GetBreakdownQuery(factory, registry, NullLogger<GetBreakdownQuery>.Instance)
            .ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(source, response.Source);
        Assert.Equal(count, Assert.Single(response.Rows).Value);
    }

    public async Task DisposeAsync()
    {
        await using (Octonica.ClickHouseClient.ClickHouseConnection connection = new(ClickHouseTestSupport.ConnectionString))
        {
            await connection.OpenAsync();
            await using Octonica.ClickHouseClient.ClickHouseCommand command = connection.CreateCommand(
                "SELECT uuid FROM system.tables WHERE database = 'nealytics_core' AND name = {table:String}");
            command.Parameters.Add(new Octonica.ClickHouseClient.ClickHouseParameter
            {
                ParameterName = "table",
                Value = "rollup_" + _name
            });
            if (await command.ExecuteScalarAsync() is Guid table)
            {
                _ownedTables.Add(table);
            }
        }
        await ClickHouseTestSupport.ExecuteAsync($"DROP VIEW IF EXISTS nealytics_core.rollup_{_name}_mv");
        await ClickHouseTestSupport.ExecuteAsync($"DROP TABLE IF EXISTS nealytics_core.rollup_{_name} SYNC");
        foreach (Guid table in _ownedTables)
        {
            await ClickHouseTestSupport.ExecuteAsync(
                $"ALTER TABLE {ClickHouseRollupReadiness.TableName} DELETE WHERE table_id = toUUID('{table}') SETTINGS mutations_sync = 2");
        }
        await _project.DisposeAsync();
        foreach (ClickHouseConnectionFactory factory in _factories)
        {
            await factory.DisposeAsync();
        }
    }

    private sealed class ErrorLogger : ILogger<ClickHouseSchemaMigrator>
    {
        public List<Exception> Errors { get; } = [];
        public List<(int Id, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((eventId.Id, formatter(state, exception)));
            if (exception is not null)
            {
                Errors.Add(exception);
            }
        }
    }
}

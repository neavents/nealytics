using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Rollups;
using Octonica.ClickHouseClient.Exceptions;

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
    public async Task HistoricalCompletionSurvivesFreshStartupWithoutReinserting()
    {
        await SeedAsync();
        var first = Build();
        Assert.Empty(first.Registry.Routable);
        await first.Migrator.StartAsync(CancellationToken.None);
        Assert.True(first.Registry.IsRoutable(first.Registry.Declared[0]));
        Assert.Equal(2, await StoredEventsAsync());
        await AssertResultAsync(first.Registry, first.Factory, "rollup:" + _name, 2);

        var restarted = Build();
        await restarted.Migrator.StartAsync(CancellationToken.None);
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ClickHouseRollupReadiness(pooled).CaptureAsync(rollup, CancellationToken.None));
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
            await ClickHouseTestSupport.ExecuteAsync($"DROP TABLE nealytics_core.{rollup.TableName} SYNC");
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

    private (ClickHouseSchemaMigrator Migrator, RollupRegistry Registry, ClickHouseConnectionFactory Factory) Build(
        bool backfill = true,
        string eventTypes = "ready_e",
        string? connectionString = null,
        ILogger<ClickHouseSchemaMigrator>? logger = null)
    {
        TelemetryEngineOptions options = new()
        {
            ClickHouseConnectionString = connectionString ?? ClickHouseTestSupport.ConnectionString,
            ConnectionPoolSize = 2,
            BackfillRollups = backfill,
            Rollups = [new RollupOptions { Name = _name, Grain = "day", EventTypes = eventTypes }]
        };
        ClickHouseConnectionFactory factory = new(Options.Create(options));
        _factories.Add(factory);
        DimensionRegistry dimensions = new(options);
        MeasureRegistry measures = new(options, dimensions);
        RollupRegistry registry = new(options, dimensions, measures);
        ClickHouseSchemaMigrator migrator = new(factory, dimensions, measures, registry, Options.Create(options),
            logger ?? NullLogger<ClickHouseSchemaMigrator>.Instance);
        return (migrator, registry, factory);
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
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Errors.Add(exception);
            }
        }
    }
}

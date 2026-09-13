using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Integration;

/// <summary>
/// A rollup declared after data exists must answer exactly what the raw table answers, for every
/// traffic class, or a chart silently reads low for the whole window the rollup did not cover.
/// </summary>
[Collection("ClickHouse")]
public class RollupBackfillIntegrationTests : IAsyncLifetime
{
    private const string Project = "p-rollup-backfill";
    private readonly string _tenant = $"t-{Guid.NewGuid():N}";
    private readonly string _name = $"probe_{Guid.NewGuid():N}"[..20];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await ClickHouseTestSupport.ExecuteAsync($"DROP VIEW IF EXISTS nealytics_core.rollup_{_name}_mv");
        await ClickHouseTestSupport.ExecuteAsync($"DROP TABLE IF EXISTS nealytics_core.rollup_{_name}");
        await ClickHouseTestSupport.DeleteProjectsAsync(Project);
    }

    private static TelemetryEngineOptions Options(RollupOptions rollup) => new()
    {
        ClickHouseConnectionString = ClickHouseTestSupport.ConnectionString,
        ConnectionPoolSize = 2,
        Rollups = [rollup],
    };

    private static (ClickHouseSchemaMigrator Migrator, RollupRegistry Registry, ClickHouseConnectionFactory Factory) Build(
        TelemetryEngineOptions options)
    {
        ClickHouseConnectionFactory factory = new(Microsoft.Extensions.Options.Options.Create(options));
        DimensionRegistry dimensions = new(options);
        MeasureRegistry measures = new(options, dimensions);
        RollupRegistry rollups = new(options, dimensions, measures);
        ClickHouseSchemaMigrator migrator = new(
            factory, dimensions, measures, rollups,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<ClickHouseSchemaMigrator>.Instance);
        return (migrator, rollups, factory);
    }

    private static readonly DateTime Early = DateTime.UtcNow.Date.AddDays(-40);
    private static readonly DateTime Late = DateTime.UtcNow.Date.AddDays(-5);
    private static readonly DateTime WindowFrom = Early.AddDays(-1);
    private static readonly DateTime WindowTo = Late.AddDays(2);

    private static string At(DateTime day, int hour) =>
        day.AddHours(hour).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    private async Task SeedAsync()
    {
        string rows = string.Join(", ",
            Row("s1", "view", "normal", At(Early, 10)),
            Row("s1", "view", "normal", At(Early, 11)),
            Row("s2", "view", "normal", At(Early, 12)),
            Row("s2", "open", "normal", At(Early.AddDays(1), 9)),
            Row("s3", "view", "normal", At(Late, 12)),
            Row("bot", "view", "bot", At(Early, 13)),
            Row("bot", "view", "bot", At(Late, 13)));

        await ClickHouseTestSupport.ExecuteAsync(
            "INSERT INTO nealytics_core.global_events "
            + "(event_id, project_id, tenant_id, session_id, event_type, timestamp, traffic_class, object_id) VALUES " + rows);
    }

    private string Row(string session, string eventType, string traffic, string at) =>
        $"(generateUUIDv4(), '{Project}', '{_tenant}', '{session}', '{eventType}', toDateTime64('{at}', 3, 'UTC'), '{traffic}', 'obj')";

    private static async Task<long> ScalarAsync(string sql)
    {
        await using ClickHouseConnection connection = new(ClickHouseTestSupport.ConnectionString);
        await connection.OpenAsync();
        await using ClickHouseCommand command = connection.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ANewRollupIsBackfilledFromEveryPartition_KeyedOnTrafficClass()
    {
        await SeedAsync();
        RollupOptions declaration = new() { Name = _name, Grain = "day", EventTypes = "", Dimensions = "object_id", Measures = "" };
        (ClickHouseSchemaMigrator migrator, RollupRegistry registry, _) = Build(Options(declaration));

        await migrator.StartAsync(CancellationToken.None);

        registry.IsRoutable(registry.Declared[0]).Should().BeTrue();

        long normalEvents = await ScalarAsync(
            $"SELECT countMerge(events) FROM nealytics_core.rollup_{_name} WHERE tenant_id = '{_tenant}' AND traffic_class = 'normal'");
        long botEvents = await ScalarAsync(
            $"SELECT countMerge(events) FROM nealytics_core.rollup_{_name} WHERE tenant_id = '{_tenant}' AND traffic_class = 'bot'");
        long normalSessions = await ScalarAsync(
            $"SELECT uniqExactMerge(sessions) FROM nealytics_core.rollup_{_name} WHERE tenant_id = '{_tenant}' AND traffic_class = 'normal' AND event_type = 'view'");

        normalEvents.Should().Be(5, "every partition was backfilled");
        botEvents.Should().Be(2, "bots are stored under their own key rather than mixed in or dropped");
        normalSessions.Should().Be(3);
    }

    [Fact]
    public async Task ABreakdownRoutedToTheBackfilledRollupMatchesRaw_AndExcludesBotsByDefault()
    {
        await SeedAsync();
        RollupOptions declaration = new() { Name = _name, Grain = "day", EventTypes = "", Dimensions = "object_id", Measures = "" };
        (ClickHouseSchemaMigrator migrator, RollupRegistry registry, ClickHouseConnectionFactory factory) = Build(Options(declaration));
        await migrator.StartAsync(CancellationToken.None);

        BreakdownRequest request = new()
        {
            ProjectId = Project,
            TenantId = _tenant,
            Metric = BreakdownMetric.Sessions,
            MetricWire = "sessions",
            GroupByColumn = "object_id",
            EventType = "view",
            Filters = [],
            TrafficClass = "normal",
            From = WindowFrom,
            To = WindowTo,
            Limit = 10,
            Order = BreakdownOrder.ValueDescending,
        };

        GetBreakdownQuery query = new(factory, registry, NullLogger<GetBreakdownQuery>.Instance);
        BreakdownResponse routed = await query.ExecuteAsync(request, CancellationToken.None);

        routed.Source.Should().Be("rollup:" + _name);
        routed.Rows.Should().ContainSingle().Which.Value.Should().Be(3, "the bot session is not a visit");

        registry.MarkUnroutable(registry.Declared[0], "test");
        BreakdownResponse raw = await query.ExecuteAsync(request, CancellationToken.None);

        raw.Source.Should().Be("raw");
        raw.Rows.Should().ContainSingle().Which.Value.Should().Be(3);
    }

    [Fact]
    public async Task ARollupWhoseStoredShapeDiffersIsLeftAloneAndNotRouted()
    {
        await SeedAsync();
        (ClickHouseSchemaMigrator first, _, _) = Build(Options(new RollupOptions
        {
            Name = _name, Grain = "day", EventTypes = "", Dimensions = "object_id", Measures = "",
        }));
        await first.StartAsync(CancellationToken.None);

        (ClickHouseSchemaMigrator second, RollupRegistry registry, _) = Build(Options(new RollupOptions
        {
            Name = _name, Grain = "day", EventTypes = "", Dimensions = "object_id,os", Measures = "",
        }));
        await second.StartAsync(CancellationToken.None);

        registry.IsRoutable(registry.Declared[0]).Should().BeFalse();
        registry.Routable.Should().BeEmpty();
    }

    [Fact]
    public async Task APivotRoutedToTheRollupAgreesWithRaw()
    {
        await SeedAsync();
        RollupOptions declaration = new() { Name = _name, Grain = "day", EventTypes = "", Dimensions = "object_id", Measures = "" };
        (ClickHouseSchemaMigrator migrator, RollupRegistry registry, ClickHouseConnectionFactory factory) = Build(Options(declaration));
        await migrator.StartAsync(CancellationToken.None);

        PivotRequest request = new()
        {
            ProjectId = Project,
            TenantId = _tenant,
            GroupByColumn = "object_id",
            Metrics =
            [
                new PivotMetric { Kind = PivotMetricKind.Events, Wire = "events:view", EventType = "view" },
                new PivotMetric { Kind = PivotMetricKind.Sessions, Wire = "sessions:open", EventType = "open" },
                new PivotMetric { Kind = PivotMetricKind.Sessions, Wire = "sessions" },
            ],
            Filters = [],
            TrafficClass = "normal",
            From = WindowFrom,
            To = WindowTo,
            Limit = 10,
            Descending = true,
        };

        GetPivotQuery query = new(factory, registry, NullLogger<GetPivotQuery>.Instance);
        PivotResponse routed = await query.ExecuteAsync(request, CancellationToken.None);
        registry.MarkUnroutable(registry.Declared[0], "test");
        PivotResponse raw = await query.ExecuteAsync(request, CancellationToken.None);

        routed.Source.Should().Be("rollup:" + _name);
        raw.Source.Should().Be("raw");
        routed.Rows.Should().ContainSingle().Which.Values.Should().Equal(4, 1, 3);
        raw.Rows.Should().ContainSingle().Which.Values.Should().Equal(4, 1, 3);
        routed.Totals.Should().Equal(raw.Totals);
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Integration;

/// <summary>
/// The reconciler's guards, run against a real ClickHouse.
///
/// These were demonstrated by hand once — column added, boot refused on a type change, boot
/// refused on a deleted declaration, boot clean once retired. A guard proven once and never again
/// is a guard that quietly stops working, so each scenario is pinned here.
///
/// Every test creates its own probe column with a unique name and drops it in a finally, so a
/// failure cannot leave the shared table in a state that refuses the next boot.
/// </summary>
[Collection("ClickHouse")]
public class SchemaReconcilerGuardTests
{
    private const string Table = "nealytics_core.global_events";

    private static string ProbeName() => $"probe_{Guid.NewGuid():N}"[..24];

    private static IOptions<TelemetryEngineOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new TelemetryEngineOptions
        {
            ClickHouseConnectionString = ClickHouseTestSupport.ConnectionString,
            ConnectionPoolSize = 2,
        });

    /// <summary>
    /// The dimensions the environment declares, read from the same variables production reads.
    ///
    /// <remarks>
    /// <para>Without this, every test here declared <i>only</i> its own probe column — so the
    /// reconciler saw the deployment's real dimensions as undeclared. While those columns were
    /// empty that was invisible. The moment one held a single row, three of these tests failed with
    /// "Undeclared column(s) ... menu_id (1 row(s))" and the two refusal tests started passing for a
    /// reason that had nothing to do with their probe. Both directions are wrong, and the second is
    /// worse: a guard test that would pass with the guard broken.</para>
    ///
    /// <para>The table is shared with whatever deployment owns this ClickHouse, so isolating the
    /// probe means declaring everything else that legitimately exists. Reading it from the
    /// environment rather than naming columns here keeps the engine's own suite free of any
    /// deployment's vocabulary — which is the property the whole change was for.</para>
    /// </remarks>
    /// </summary>
    private static List<DimensionOptions> AmbientDimensions()
    {
        List<DimensionOptions> ambient = [];

        for (int i = 0; ; i++)
        {
            string? name = Environment.GetEnvironmentVariable($"TelemetryEngine__Dimensions__{i}__Name");
            if (string.IsNullOrWhiteSpace(name)) break;

            ambient.Add(new DimensionOptions
            {
                Name = name,
                Type = Environment.GetEnvironmentVariable($"TelemetryEngine__Dimensions__{i}__Type") ?? "String",
                Retired = string.Equals(
                    Environment.GetEnvironmentVariable($"TelemetryEngine__Dimensions__{i}__Retired"),
                    "true",
                    StringComparison.OrdinalIgnoreCase),
            });
        }

        return ambient;
    }

    private static async Task<ClickHouseSchemaMigrator> MigratorAsync(params DimensionOptions[] declared)
    {
        List<DimensionOptions> dimensions = AmbientDimensions();

        // The probe wins if it collides with an ambient name — a test that names its own probe is
        // being explicit, and two entries with one name refuses the boot for an unrelated reason.
        foreach (DimensionOptions declaration in declared)
        {
            dimensions.RemoveAll(d => string.Equals(d.Name, declaration.Name, StringComparison.Ordinal));
            dimensions.Add(declaration);
        }

        TelemetryEngineOptions options = new()
        {
            ClickHouseConnectionString = ClickHouseTestSupport.ConnectionString,
            ConnectionPoolSize = 2,
            Dimensions = dimensions,
        };

        ClickHouseConnectionFactory factory = new(
            Microsoft.Extensions.Options.Options.Create(options));

        await Task.CompletedTask;

        return new ClickHouseSchemaMigrator(
            factory,
            new DimensionRegistry(options),
            NullLogger<ClickHouseSchemaMigrator>.Instance);
    }

    private static Task DropProbeAsync(string name) =>
        ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE {Table} DROP COLUMN IF EXISTS {name}");

    private static async Task<string?> ColumnTypeAsync(string name)
    {
        await using Octonica.ClickHouseClient.ClickHouseConnection connection =
            new(ClickHouseTestSupport.ConnectionString);
        await connection.OpenAsync();
        await using Octonica.ClickHouseClient.ClickHouseCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT type FROM system.columns WHERE database='nealytics_core' "
            + "AND table='global_events' AND name={n:String}";
        command.Parameters.Add(new Octonica.ClickHouseClient.ClickHouseParameter
        {
            ParameterName = "n",
            Value = name,
        });
        return (string?)await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task DeclaredButMissing_AddsTheColumn_AndStartsCleanly()
    {
        string probe = ProbeName();

        try
        {
            ClickHouseSchemaMigrator migrator = await MigratorAsync(
                new DimensionOptions { Name = probe, Type = "String" });

            await migrator.StartAsync(CancellationToken.None);

            (await ColumnTypeAsync(probe)).Should().Be("Nullable(String)",
                "a declared dimension with no column must be created, metadata-only and instant");
        }
        finally
        {
            await DropProbeAsync(probe);
        }
    }

    [Fact]
    public async Task DeclaredTypeDiffersFromColumn_RefusesToStart_NamingBothTypes()
    {
        string probe = ProbeName();

        try
        {
            await ClickHouseTestSupport.ExecuteAsync(
                $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {probe} Nullable(String)");

            ClickHouseSchemaMigrator migrator = await MigratorAsync(
                new DimensionOptions { Name = probe, Type = "UInt64" });

            Func<Task> start = () => migrator.StartAsync(CancellationToken.None);

            // Coercing instead would either drop every existing value or start writing values the
            // old rows cannot be compared against. A refused boot is recoverable in a minute.
            (await start.Should().ThrowAsync<ClickHouseSchemaMigrator.SchemaReconciliationException>())
                .Which.Message.Should()
                    .Contain(probe).And
                    .Contain("Nullable(UInt64)").And
                    .Contain("Nullable(String)");
        }
        finally
        {
            await DropProbeAsync(probe);
        }
    }

    [Fact]
    public async Task UndeclaredColumnHoldingData_RefusesToStart_NamingTheRowCount()
    {
        string probe = ProbeName();
        string projectId = $"p-guard-{Guid.NewGuid():N}";

        try
        {
            await ClickHouseTestSupport.ExecuteAsync(
                $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {probe} Nullable(String)");
            await ClickHouseTestSupport.ExecuteAsync(
                $"INSERT INTO {Table} (event_id, project_id, tenant_id, session_id, event_type, "
                + $"{probe}, metadata_json, timestamp) VALUES (generateUUIDv4(), '{projectId}', "
                + $"'t', 's', 'e', 'a-value', '{{}}', now64(3))");

            // The probe is not declared — this is one deleted config line, the mistake the guard
            // exists for. Everything the environment declares still is, so the probe is the only
            // undeclared column and the refusal can only be about it.
            ClickHouseSchemaMigrator migrator = await MigratorAsync();

            Func<Task> start = () => migrator.StartAsync(CancellationToken.None);

            string message =
                (await start.Should().ThrowAsync<ClickHouseSchemaMigrator.SchemaReconciliationException>())
                    .Which.Message;

            message.Should()
                .Contain(probe).And
                .Contain("1 row(s)",
                    "the refusal must say how much data is at stake, not just that something is wrong");

            // Without this the test passes whenever ANY dimension column holds data, probe or not —
            // which is how it survived before AmbientDimensions() existed.
            foreach (DimensionOptions ambient in AmbientDimensions())
            {
                message.Should().NotContain(ambient.Name!,
                    "a declared dimension must never appear in an undeclared-column refusal");
            }
        }
        finally
        {
            await ClickHouseTestSupport.ExecuteAsync(
                $"ALTER TABLE {Table} DELETE WHERE project_id = '{projectId}' SETTINGS mutations_sync=2");
            await DropProbeAsync(probe);
        }
    }

    [Fact]
    public async Task RetiredDimension_StartsCleanly_AndKeepsTheColumnAndItsRows()
    {
        string probe = ProbeName();
        string projectId = $"p-guard-{Guid.NewGuid():N}";

        try
        {
            await ClickHouseTestSupport.ExecuteAsync(
                $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {probe} Nullable(String)");
            await ClickHouseTestSupport.ExecuteAsync(
                $"INSERT INTO {Table} (event_id, project_id, tenant_id, session_id, event_type, "
                + $"{probe}, metadata_json, timestamp) VALUES (generateUUIDv4(), '{projectId}', "
                + $"'t', 's', 'e', 'a-value', '{{}}', now64(3))");

            ClickHouseSchemaMigrator migrator = await MigratorAsync(
                new DimensionOptions { Name = probe, Type = "String", Retired = true });

            await migrator.StartAsync(CancellationToken.None);

            (await ColumnTypeAsync(probe)).Should().Be("Nullable(String)",
                "retiring keeps the column — this service never drops one");
            (await ClickHouseTestSupport.CountAsync(projectId)).Should().Be(1,
                "retiring keeps every row; it stops collection, it is not a delete");
        }
        finally
        {
            await ClickHouseTestSupport.ExecuteAsync(
                $"ALTER TABLE {Table} DELETE WHERE project_id = '{projectId}' SETTINGS mutations_sync=2");
            await DropProbeAsync(probe);
        }
    }

    [Fact]
    public async Task UndeclaredColumnWithNoData_IsLeftAlone_AndDoesNotBlockStartup()
    {
        // An empty stray column is not evidence of a mistake and holds nothing to lose, so it must
        // not hold the service down. It is still never dropped automatically.
        string probe = ProbeName();

        try
        {
            await ClickHouseTestSupport.ExecuteAsync(
                $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {probe} Nullable(String)");

            ClickHouseSchemaMigrator migrator = await MigratorAsync();

            await migrator.StartAsync(CancellationToken.None);

            (await ColumnTypeAsync(probe)).Should().Be("Nullable(String)");
        }
        finally
        {
            await DropProbeAsync(probe);
        }
    }

    [Fact]
    public async Task UnreachableDatabase_DoesNotRefuseStartup()
    {
        // Deliberately different from a schema disagreement: a database blip must not take
        // analytics reads down because the write path could not be widened.
        TelemetryEngineOptions options = new()
        {
            ClickHouseConnectionString = "Host=127.0.0.1;Port=59999;Database=nealytics_core;",
            ConnectionPoolSize = 1,
            Dimensions = [new DimensionOptions { Name = "probe_unreachable", Type = "String" }],
        };

        ClickHouseSchemaMigrator migrator = new(
            new ClickHouseConnectionFactory(Microsoft.Extensions.Options.Options.Create(options)),
            new DimensionRegistry(options),
            NullLogger<ClickHouseSchemaMigrator>.Instance);

        Func<Task> start = () => migrator.StartAsync(CancellationToken.None);

        await start.Should().NotThrowAsync();
    }
}

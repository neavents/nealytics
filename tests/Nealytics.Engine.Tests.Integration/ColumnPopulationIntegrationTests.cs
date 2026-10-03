using FluentAssertions;
using Nealytics.Engine.Features.GetSchema;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Integration;

/// <summary>
/// The population census, run by the real server.
///
/// <b>Why this cannot be a unit test.</b> The unit suite pins the SQL <i>text</i>, which proves the
/// right expression was built and nothing about whether ClickHouse accepts it. The specific hazard
/// is <c>coalesce</c> over <c>LowCardinality(String)</c>: that column is not nullable, it defaults
/// to <c>''</c>, and ClickHouse is particular about wrapping LowCardinality in nullable-shaped
/// functions. A string assertion is blind to all of it, and the failure would land on the first
/// real <c>/schema</c> call in production.
///
/// <b>What the census is for.</b> A declared dimension nothing fills is indistinguishable, at every
/// other layer, from a tenant with no traffic: config valid, column present, query returns 200, zero
/// rows. Four dimensions in one deployment sat in exactly that state for the whole
/// retention window because the producer filling them was written and never deployed. Nothing
/// reported a problem — the only component able to tell those apart is the one holding the rows.
///
/// <b>Serialised with the rest of the ClickHouse classes, and not optionally.</b> This class adds
/// probe columns and then puts rows in them. <c>SchemaReconcilerGuardTests</c> boots real
/// migrators, and a migrator that meets an undeclared column holding data refuses to start — by
/// design. Run in parallel, that is a genuine failure with a correct error message, arriving in a
/// class that did nothing wrong, on some runs and not others. A suite reports that shape as
/// flakiness; it is the guard working on a race the test suite created.
/// </summary>
[Collection("ClickHouse")]
public class ColumnPopulationIntegrationTests : IAsyncLifetime
{
    private const string Table = "nealytics_core.global_events";

    private const string Project = "p-pop";
    private const string Tenant = "t-pop";

    private const string StringDim = "pop_probe_str";
    private const string LowCardDim = "pop_probe_lc";
    private const string NumericDim = "pop_probe_num";
    private const string FilledMeasure = "pop_probe_m_filled";
    private const string EmptyMeasure = "pop_probe_m_empty";

    public async Task InitializeAsync()
    {
        await ClickHouseTestSupport.ExecuteAsync(
            $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {StringDim} Nullable(String)");
        await ClickHouseTestSupport.ExecuteAsync(
            $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {LowCardDim} LowCardinality(String) DEFAULT ''");
        await ClickHouseTestSupport.ExecuteAsync(
            $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {NumericDim} Nullable(UInt64)");
        await ClickHouseTestSupport.ExecuteAsync(
            $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {FilledMeasure} Nullable(UInt32)");
        await ClickHouseTestSupport.ExecuteAsync(
            $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {EmptyMeasure} Nullable(UInt32)");

        await ClickHouseTestSupport.DeleteProjectsAsync(Project);
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await ClickHouseTestSupport.DeleteProjectsAsync(Project);

        foreach (string column in new[] { StringDim, LowCardDim, NumericDim, FilledMeasure, EmptyMeasure })
        {
            await ClickHouseTestSupport.ExecuteAsync(
                $"ALTER TABLE {Table} DROP COLUMN IF EXISTS {column}");
        }
    }

    /// <summary>
    /// Three rows. The string dimension is filled on one and empty-string on another, which is the
    /// distinction the census exists to make: a producer that sends the key with no value writes
    /// <c>''</c>, and <c>count()</c> would report that as data.
    /// </summary>
    private static async Task SeedAsync()
    {
        await ClickHouseTestSupport.ExecuteAsync(
            $"INSERT INTO {Table} "
            + $"(event_id, project_id, tenant_id, session_id, event_type, timestamp, "
            + $"{StringDim}, {LowCardDim}, {NumericDim}, {FilledMeasure}) VALUES "
            + $"(generateUUIDv4(), '{Project}', '{Tenant}', 's1', 'probe', now64(3), 'value', 'tr', 7, 100), "
            + $"(generateUUIDv4(), '{Project}', '{Tenant}', 's2', 'probe', now64(3), '', '', NULL, NULL), "
            + $"(generateUUIDv4(), '{Project}', '{Tenant}', 's3', 'probe', now64(3), NULL, '', 9, 250)");
    }

    private static GetColumnPopulationQuery Query()
    {
        TelemetryEngineOptions options = new()
        {
            ClickHouseConnectionString = ClickHouseTestSupport.ConnectionString,
            ConnectionPoolSize = 2,
            Dimensions =
            [
                new DimensionOptions { Name = StringDim, Type = "String" },
                new DimensionOptions { Name = LowCardDim, Type = "LowCardinality" },
                new DimensionOptions { Name = NumericDim, Type = "UInt64" },
            ],
            Measures =
            [
                new MeasureOptions { Name = FilledMeasure, Type = "UInt32", Aggregations = "sum" },
                new MeasureOptions { Name = EmptyMeasure, Type = "UInt32", Aggregations = "sum" },
            ],
        };

        DimensionRegistry dimensions = new(options);

        return new GetColumnPopulationQuery(
            new ClickHouseConnectionFactory(Microsoft.Extensions.Options.Options.Create(options)),
            dimensions,
            new MeasureRegistry(options, dimensions));
    }

    private static Task<IReadOnlyDictionary<string, long>> RunAsync() =>
        Query().ExecuteAsync(Project, Tenant, DateTime.UtcNow, CancellationToken.None);

    [Fact]
    public async Task TheCensusRunsAtAllOnRealClickHouse()
    {
        // The whole point of this file. coalesce() over LowCardinality(String) is the expression a
        // string-matching unit test cannot vouch for.
        IReadOnlyDictionary<string, long> counts = await RunAsync();

        counts.Should().ContainKey(LowCardDim);
        counts.Should().HaveCount(5, "every declared dimension and measure is censused in one pass");
    }

    [Fact]
    public async Task AnEmptyStringIsNotCountedAsData()
    {
        // Row 1 holds 'value', row 2 holds '', row 3 holds NULL. Only row 1 is a collected value.
        (await RunAsync())[StringDim].Should().Be(
            1,
            "a producer that sends the key with no value writes '', and counting that as data is "
            + "how a dimension nobody fills reads as healthy");
    }

    [Fact]
    public async Task ALowCardinalityColumnCountsOnlyItsNonDefaultRows()
    {
        // LowCardinality(String) is NOT nullable -- it defaults to ''. count() over it would return
        // every row in the table, reporting a completely unfilled column as fully populated.
        (await RunAsync())[LowCardDim].Should().Be(1, "only one row carries a locale");
    }

    [Fact]
    public async Task ANullableNumericCountsNonNullAndKeepsZero()
    {
        // Rows 1 and 3 carry 7 and 9; row 2 is NULL. 0 would be a legitimate value here, which is
        // why this cannot use the empty-string rule.
        (await RunAsync())[NumericDim].Should().Be(2);
    }

    [Fact]
    public async Task AMeasureNothingSendsReportsZeroRatherThanFailing()
    {
        // The exact shape of a real deployment's finding: dwell_ms declared, reconciled, and NULL on every
        // row because the value was still going to metadata_json.
        IReadOnlyDictionary<string, long> counts = await RunAsync();

        counts[FilledMeasure].Should().Be(2);
        counts[EmptyMeasure].Should().Be(0);
    }

    [Fact]
    public async Task AnotherTenantsRowsAreNotCounted()
    {
        // The census is a read like any other and carries the same unconditional tenant predicate.
        // Reporting a column as populated because some other tenant fills it would be worse than
        // not reporting it at all.
        IReadOnlyDictionary<string, long> counts =
            await Query().ExecuteAsync(Project, "t-someone-else", DateTime.UtcNow, CancellationToken.None);

        counts[StringDim].Should().Be(0);
        counts[NumericDim].Should().Be(0);
    }

    [Fact]
    public async Task TheCountsAreCachedRatherThanScannedPerRequest()
    {
        // A full scan per declared column per dashboard load is the cost pattern the rollups exist
        // to avoid. One instance, two calls, second served from cache: a row inserted in between
        // must not appear.
        GetColumnPopulationQuery query = Query();
        DateTime now = DateTime.UtcNow;

        long before = (await query.ExecuteAsync(Project, Tenant, now, CancellationToken.None))[StringDim];

        await ClickHouseTestSupport.ExecuteAsync(
            $"INSERT INTO {Table} (event_id, project_id, tenant_id, session_id, event_type, timestamp, {StringDim}) "
            + $"VALUES (generateUUIDv4(), '{Project}', '{Tenant}', 's4', 'probe', now64(3), 'another')");

        long cached = (await query.ExecuteAsync(Project, Tenant, now, CancellationToken.None))[StringDim];
        cached.Should().Be(before, "within the cache lifetime the census is not re-run");

        long refreshed = (await query.ExecuteAsync(
            Project, Tenant, now.AddMinutes(6), CancellationToken.None))[StringDim];
        refreshed.Should().Be(before + 1, "past it, the census reflects the new row");
    }
}

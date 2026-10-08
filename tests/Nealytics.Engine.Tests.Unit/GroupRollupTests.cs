using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

public class GroupRollupTests
{
    private static readonly DateTime Midnight = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NextMidnight = new(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TenantSet Org = new() { Attribute = "parent", Value = "org-1" };

    private static TelemetryEngineOptions Options(params RollupOptions[] rollups) => new()
    {
        Dimensions = [new DimensionOptions { Name = "widget_id" }, new DimensionOptions { Name = "currency", Type = "LowCardinality" }],
        Measures = [new MeasureOptions { Name = "amount", Type = "Decimal", Aggregations = "sum,avg,p50,p95", UnitDimension = "currency" }],
        TenantAttributes = [new TenantAttributeOptions { Name = "parent" }, new TenantAttributeOptions { Name = "region" }],
        Rollups = [.. rollups],
    };

    private static RollupRegistry Registry(params RollupOptions[] rollups)
    {
        TelemetryEngineOptions options = Options(rollups);
        DimensionRegistry dimensions = new(options);
        RollupRegistry registry = new(options, dimensions, new MeasureRegistry(options, dimensions), new TenantAttributeRegistry(options));
        return registry;
    }

    private static RollupOptions PerTenant() => new()
    {
        Name = "daily", Grain = "day", Dimensions = "widget_id,currency", Measures = "amount:sum",
    };

    private static RollupOptions ByOrg() => new()
    {
        Name = "daily_org", Grain = "day", Dimensions = "currency", Measures = "amount:sum,amount:p95",
        TenantAttributes = "parent,region",
    };

    private static BreakdownRequest Request(string groupBy, string tenantId, TenantSet? set, string metric = "events") => new()
    {
        ProjectId = "p",
        TenantId = tenantId,
        TenantSet = set,
        Metric = metric == "events" ? BreakdownMetric.Events : BreakdownMetric.Measure,
        MeasureColumn = metric == "events" ? null : "amount",
        MeasureFunction = metric == "events" ? null : metric,
        MeasureAggregation = metric == "events" ? null : metric,
        MetricWire = metric,
        GroupByColumn = groupBy,
        Filters = [],
        From = Midnight,
        To = NextMidnight,
        Limit = 10,
        TrafficClass = "normal",
    };

    [Fact]
    public void AGroupRollup_KeysOnTheAttributesInsteadOfTheTenant()
    {
        Rollup rollup = Registry(ByOrg()).Declared.Single();

        rollup.IsGroupRollup.Should().BeTrue();
        string table = RollupRegistry.BuildTableDdl(rollup);
        table.Should().Contain("tenant_attr_parent String, tenant_attr_region String, bucket");
        table.Should().NotContain("tenant_id");
        table.Should().Contain("ORDER BY (project_id, tenant_attr_parent, tenant_attr_region, bucket, event_type, traffic_class, currency)");
    }

    [Fact]
    public void AGroupRollupView_JoinsEachEventToItsTenantsAttributesAtInsert()
    {
        string view = RollupRegistry.BuildViewDdl(Registry(ByOrg()).Declared.Single());

        view.Should().Contain("LEFT ANY JOIN (SELECT project_id, tenant_id, anyIf(value, attribute = 'parent') AS tenant_attr_parent");
        view.Should().Contain("FROM nealytics_core.tenant_attributes FINAL WHERE attribute IN ('parent', 'region')");
        view.Should().Contain("quantilesState(0.5, 0.75, 0.9, 0.95, 0.99)(source.amount) AS amount_quantiles");
    }

    [Fact]
    public void APerTenantRollup_IsByteForByteUnchanged()
    {
        Rollup rollup = Registry(PerTenant()).Declared.Single();

        RollupRegistry.BuildTableDdl(rollup).Should().Be(
            "CREATE TABLE IF NOT EXISTS nealytics_core.rollup_daily (project_id LowCardinality(String), tenant_id String, "
            + "bucket DateTime('UTC'), event_type LowCardinality(String), traffic_class LowCardinality(String), "
            + "widget_id String, currency LowCardinality(String), events AggregateFunction(count), "
            + "sessions AggregateFunction(uniqExact, String), users AggregateFunction(uniqExact, Nullable(String)), "
            + "amount_sum AggregateFunction(sum, Nullable(Decimal(18, 4)))) ENGINE = AggregatingMergeTree "
            + "PARTITION BY toYYYYMM(bucket) ORDER BY (project_id, tenant_id, bucket, event_type, traffic_class, widget_id, currency)");
    }

    [Fact]
    public void AnUndeclaredAttribute_RefusesTheBoot()
    {
        RollupOptions rollup = ByOrg();
        rollup.TenantAttributes = "brand";

        Action build = () => Registry(rollup);
        build.Should().Throw<InvalidOperationException>().WithMessage("*not declared under TelemetryEngine:TenantAttributes*");
    }

    [Fact]
    public void ASessionRollup_CannotGroupTenants()
    {
        RollupOptions rollup = ByOrg();
        rollup.Grain = "session";
        rollup.Measures = "";

        Action build = () => Registry(rollup);
        build.Should().Throw<InvalidOperationException>().WithMessage("*session rollup*");
    }

    [Fact]
    public void ASetQuery_PrefersTheGroupRollup()
    {
        RollupPlan? plan = RollupPlanner.Select(Request("currency", string.Empty, Org), Registry(PerTenant(), ByOrg()));

        plan.Should().NotBeNull();
        plan!.Value.Rollup.Name.Should().Be("daily_org");
    }

    [Fact]
    public void ASingleTenantQuery_NeverReadsAGroupRollup()
    {
        RollupPlanner.Select(Request("currency", "t", null), Registry(ByOrg())).Should().BeNull();
    }

    [Fact]
    public void ANarrowedSetQuery_NeverReadsAGroupRollup()
    {
        RollupPlanner.Select(Request("currency", "v7", Org), Registry(ByOrg())).Should().BeNull();
    }

    [Fact]
    public void ASetOnAnAttributeTheGroupRollupLacks_FallsBackToThePerTenantRollup()
    {
        TenantSet otherSet = new() { Attribute = "region", Value = "west" };
        RollupOptions byParentOnly = ByOrg();
        byParentOnly.TenantAttributes = "parent";

        RollupPlanner.Select(Request("currency", string.Empty, otherSet), Registry(PerTenant(), byParentOnly))!
            .Value.Rollup.Name.Should().Be("daily");
    }

    [Fact]
    public void GroupingByTenant_RoutesOnlyForASet_SoExistingResponsesKeepTheirSource()
    {
        RollupPlanner.Select(Request("tenant_id", "t", null), Registry(PerTenant())).Should().BeNull();
        RollupPlanner.Select(Request("tenant_id", string.Empty, Org), Registry(PerTenant())).Should().NotBeNull();
    }

    [Fact]
    public void AGroupRollupQuery_FiltersOnTheStoredAttributeAndGroupsByTheOther()
    {
        BreakdownRequest request = Request("tenant.region", string.Empty, Org);
        RollupRegistry registry = Registry(ByOrg());
        RollupPlan? plan = RollupPlanner.Select(request, registry);

        string sql = GetBreakdownQuery.BuildQuery(request, plan).Sql;

        sql.Should().StartWith("WITH grouped AS (SELECT tenant_attr_region AS key, countMerge(events)");
        sql.Should().Contain("FROM nealytics_core.rollup_daily_org WHERE project_id = {projectId:String} AND tenant_attr_parent = {tenantSetValue:String}");
    }

    [Fact]
    public void APercentile_IsAnsweredFromTheQuantilesState()
    {
        BreakdownRequest request = Request("currency", string.Empty, Org, "p95");
        RollupPlan? plan = RollupPlanner.Select(request, Registry(ByOrg()));

        plan!.Value.ValueExpression.Should().Be("arrayElement(quantilesMerge(0.5, 0.75, 0.9, 0.95, 0.99)(amount_quantiles), 4)");
    }

    [Fact]
    public void APivotPercentile_IsAnsweredFromTheQuantilesState()
    {
        TelemetryEngineOptions options = Options(ByOrg());
        DimensionRegistry dimensions = new(options);
        MeasureRegistry measures = new(options, dimensions);
        TenantAttributeRegistry attributes = new(options);
        RollupRegistry registry = new(options, dimensions, measures, attributes);

        PivotRequestResult parsed = PivotRequestFactory.Create(
            "p", null, "currency", ["p95(amount)", "sum(amount)"], [], Midnight.ToString("O"), NextMidnight.ToString("O"),
            null, null, null, null, null, null, new QueryColumns(dimensions, attributes), measures, 100, 24, NextMidnight, Org);

        parsed.Success.Should().BeTrue(parsed.ErrorMessage);
        PivotRollupPlan? plan = PivotRollupPlanner.Select(parsed.Request, registry);
        plan.Should().NotBeNull();
        GetPivotQuery.BuildQuery(parsed.Request, plan).Sql.Should()
            .Contain("arrayElement(quantilesMerge(0.5, 0.75, 0.9, 0.95, 0.99)(amount_quantiles), 4) AS m0")
            .And.Contain("sumMerge(amount_sum) AS m1");
    }

    [Fact]
    public void OnCluster_IsAddedToEveryStatementTheEngineIssues()
    {
        string clause = ClusterDdl.Clause("analytics");
        Rollup rollup = Registry(ByOrg()).Declared.Single();

        clause.Should().Be(" ON CLUSTER 'analytics'");
        ClickHouseSchemaMigrator.CoreStatementsFor(clause).Should().OnlyContain(s => s.Contains("global_events ON CLUSTER 'analytics' ADD"));
        RollupRegistry.BuildTableDdl(rollup, clause).Should().StartWith("CREATE TABLE IF NOT EXISTS nealytics_core.rollup_daily_org ON CLUSTER 'analytics' (");
        RollupRegistry.BuildViewDdl(rollup, clause).Should().StartWith("CREATE MATERIALIZED VIEW nealytics_core.rollup_daily_org_mv ON CLUSTER 'analytics' TO");
        TenantAttributeRegistry.BuildTableDdl(clause).Should().StartWith("CREATE TABLE IF NOT EXISTS nealytics_core.tenant_attributes ON CLUSTER 'analytics' (");
        ClickHouseSchemaMigrator.BuildRetentionDdl(30, [], clause).Should().StartWith("ALTER TABLE nealytics_core.global_events ON CLUSTER 'analytics' MODIFY TTL");
    }

    [Fact]
    public void WithoutACluster_NothingChanges()
    {
        ClusterDdl.Clause(null).Should().BeEmpty();
        ClusterDdl.Clause("").Should().BeEmpty();
        ClickHouseSchemaMigrator.CoreStatementsFor(string.Empty).Should().Equal(ClickHouseSchemaMigrator.CoreStatements);
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("x'; DROP TABLE y")]
    public void AnInjectableClusterName_RefusesTheBoot(string name)
    {
        Action act = () => ClusterDdl.Clause(name);
        act.Should().Throw<InvalidOperationException>();
    }
}

using System.Collections.Frozen;
using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public class TenantAttributeFilterTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantSet Org = new() { Attribute = "parent", Value = "org-1" };

    private static TelemetryEngineOptions Options() => new()
    {
        Dimensions = [new DimensionOptions { Name = "widget_id" }],
        TenantAttributes = [new TenantAttributeOptions { Name = "parent" }, new TenantAttributeOptions { Name = "region" }],
    };

    private static QueryColumns Columns()
    {
        TelemetryEngineOptions options = Options();
        return new QueryColumns(new DimensionRegistry(options), new TenantAttributeRegistry(options));
    }

    private static MeasureRegistry Measures()
    {
        TelemetryEngineOptions options = Options();
        return new MeasureRegistry(options, new DimensionRegistry(options));
    }

    [Fact]
    public void ADeclaredTenantAttributeIsAFilter()
    {
        FilterParser.Result parsed = FilterParser.Parse(["tenant.region:west"], Columns(), 64);

        parsed.Outcome.Should().Be(FilterParser.Outcome.Ok);
        parsed.Filters.Should().ContainSingle().Which.Should().Be(QueryFilter.Equals("tenant.region", "west"));
    }

    [Fact]
    public void AnUndeclaredTenantAttributeIsRefusedNamingWhatIsDeclared()
    {
        FilterParser.Result parsed = FilterParser.Parse(["tenant.brand:x"], Columns(), 64);

        parsed.Outcome.Should().Be(FilterParser.Outcome.UnknownColumn);
        FilterRejection.Message(parsed, Columns(), null, 64).Should().Contain("tenant.region").And.Contain("tenant.parent");
    }

    [Fact]
    public void WithoutTenantAttributesNoTenantFilterExists()
    {
        QueryColumns plain = new(new DimensionRegistry(new TelemetryEngineOptions()));

        FilterParser.Parse(["tenant.region:west"], plain, 64).Outcome.Should().Be(FilterParser.Outcome.UnknownColumn);
    }

    [Fact]
    public void ARawReadKeepsTheTenantsHoldingTheValue()
    {
        QueryScope scope = new()
        {
            ProjectId = "p",
            TenantId = string.Empty,
            TenantSet = Org,
            Filters = [QueryFilter.Equals("widget_id", "w"), QueryFilter.Equals("tenant.region", "west")],
        };
        System.Text.StringBuilder sql = new();

        ScopeClause.AppendRaw(sql, scope);

        sql.ToString().Should().EndWith(
            " AND toString(widget_id) = {filter0:String} AND tenant_id IN (SELECT tenant_id FROM nealytics_core.tenant_attributes "
            + "FINAL WHERE project_id = {projectId:String} AND attribute = {filter1Attribute:String} AND value = {filter1:String})");
        ScopeClause.Parameters(scope).Should()
            .Contain(new KeyValuePair<string, object?>("filter1Attribute", "region"))
            .And.Contain(new KeyValuePair<string, object?>("filter1", "west"))
            .And.NotContain(p => p.Key == "filter0Attribute");
    }

    private static Rollup Rollup(params string[] attributes) => new(
        "r", "rollup_r", "rollup_r_mv", RollupGrain.Day, "toStartOfDay",
        FrozenSet<string>.Empty, [], []) { TenantAttributes = attributes };

    [Fact]
    public void AGroupRollupHoldingTheAttributeFiltersItsOwnColumn()
    {
        QueryScope scope = new() { ProjectId = "p", TenantId = string.Empty, TenantSet = Org, Filters = [QueryFilter.Equals("tenant.region", "west")] };
        System.Text.StringBuilder sql = new();

        ScopeClause.AppendRollup(sql, scope, "bucket", Rollup("parent", "region"));

        sql.ToString().Should().EndWith(" AND " + RollupRegistry.TenantAttributeColumn("region") + " = {filter0:String}");
    }

    [Fact]
    public void ATenantRollupFiltersThroughMembership()
    {
        QueryScope scope = new() { ProjectId = "p", TenantId = "t", Filters = [QueryFilter.Equals("tenant.region", "west")] };
        System.Text.StringBuilder sql = new();

        ScopeClause.AppendRollup(sql, scope, "bucket", Rollup());

        sql.ToString().Should().Contain("tenant_id IN (SELECT tenant_id FROM nealytics_core.tenant_attributes");
    }

    [Fact]
    public void ARollupCoversATenantFilterOnlyWhenItCanAnswerIt()
    {
        Rollup().CoversFilter("tenant.region").Should().BeTrue();
        Rollup("parent", "region").CoversFilter("tenant.region").Should().BeTrue();
        Rollup("parent").CoversFilter("tenant.region").Should().BeFalse();
        Rollup().CoversFilter("widget_id").Should().BeFalse();
    }

    [Fact]
    public void AnOrganisationsProductMixSlicesByLegalEntity()
    {
        PivotRequestResult result = PivotRequestFactory.Create(
            "p", null, "widget_id", ["events"], ["tenant.region:west"], null, null, null, null, null, null, null, null,
            Columns(), Measures(), 1000, 24, Now, Org);

        result.Success.Should().BeTrue(result.ErrorMessage);
        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = GetPivotQuery.BuildQuery(result.Request);
        sql.Should().Contain("attribute = {filter0Attribute:String} AND value = {filter0:String}");
        parameters.Should().Contain(new KeyValuePair<string, object?>("filter0Attribute", "region"));
    }

    [Fact]
    public void ABreakdownTakesTheSameFilter()
    {
        BreakdownRequestResult result = BreakdownRequestFactory.Create(
            "p", null, "events", "widget_id", null, ["tenant.parent:org-1"], null, null, null, null, null, null, null,
            Columns(), Measures(), 1000, 24, Now, Org);

        result.Success.Should().BeTrue(result.ErrorMessage);
        GetBreakdownQuery.BuildQuery(result.Request).Sql.Should().Contain("attribute = {filter0Attribute:String}");
    }
}

using System.Security.Claims;
using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetFunnel;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Features.GetProjectTimeline;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;

namespace Nealytics.Engine.Tests.Unit;

public class TenantSetTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private static TelemetryEngineOptions Options() => new()
    {
        Dimensions = [new DimensionOptions { Name = "widget_id" }],
        TenantAttributes = [new TenantAttributeOptions { Name = "parent" }, new TenantAttributeOptions { Name = "region" }],
    };

    private static TenantAttributeRegistry Attributes() => new(Options());

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

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private static readonly TenantSet Org = new() { Attribute = "parent", Value = "org-1" };

    [Fact]
    public void ATokenWithATenant_ResolvesExactlyAsBefore()
    {
        ReadIdentity identity = ReadClaims.Resolve(Principal(("project_id", "p"), ("tenant_id", "t")), "other", Attributes());

        identity.Rejected.Should().BeFalse();
        identity.TenantId.Should().Be("t", "a single-tenant token ignores the 'tenant' parameter, as it always has");
        identity.TenantSet.Should().BeNull();
    }

    [Fact]
    public void ATokenWithATenantSet_ResolvesTheSet()
    {
        ReadIdentity identity = ReadClaims.Resolve(Principal(("project_id", "p"), ("tenant_set", "parent:org-1")), null, Attributes());

        identity.Rejected.Should().BeFalse();
        identity.TenantId.Should().BeEmpty();
        identity.TenantSet.Should().Be(Org);
    }

    [Fact]
    public void ASetTokenMayNarrowToOneTenant()
    {
        ReadIdentity identity = ReadClaims.Resolve(Principal(("project_id", "p"), ("tenant_set", "parent:org-1")), "v7", Attributes());

        identity.TenantId.Should().Be("v7");
        identity.TenantSet.Should().Be(Org);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("parent:")]
    [InlineData(":org-1")]
    [InlineData("undeclared:org-1")]
    public void AMalformedOrUndeclaredSet_IsForbidden(string claim)
    {
        ReadClaims.Resolve(Principal(("project_id", "p"), ("tenant_set", claim)), null, Attributes())
            .Outcome.Should().Be(ReadIdentityOutcome.Forbidden);
    }

    [Fact]
    public void ATokenCarryingBothATenantAndASet_IsForbidden()
    {
        ReadClaims.Resolve(Principal(("project_id", "p"), ("tenant_id", "t"), ("tenant_set", "parent:org-1")), null, Attributes())
            .Outcome.Should().Be(ReadIdentityOutcome.Forbidden, "an ambiguous token must not pick a scope for itself");
    }

    [Fact]
    public void AnOverlongNarrowingTenant_IsABadRequest()
    {
        ReadClaims.Resolve(Principal(("project_id", "p"), ("tenant_set", "parent:org-1")), new string('x', 257), Attributes())
            .Outcome.Should().Be(ReadIdentityOutcome.BadRequest);
    }

    [Fact]
    public void ASingleTenantScope_EmitsTheSameClauseAsBefore()
    {
        System.Text.StringBuilder sql = new();
        ScopeClause.AppendProjectAndTenant(sql, "t", null);

        sql.ToString().Should().Be(" WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}");
    }

    [Fact]
    public void ASetScope_FiltersTenantsThroughTheAttributeTable_SoTheSortKeyStillPrunes()
    {
        System.Text.StringBuilder sql = new();
        ScopeClause.AppendProjectAndTenant(sql, string.Empty, Org);

        sql.ToString().Should().Be(
            " WHERE project_id = {projectId:String} AND tenant_id IN (SELECT tenant_id FROM "
            + "nealytics_core.tenant_attributes FINAL WHERE project_id = {projectId:String} AND "
            + "attribute = {tenantSetAttribute:String} AND value = {tenantSetValue:String})");
    }

    [Fact]
    public void ANarrowedSetScope_RequiresBothTheTenantAndMembership()
    {
        System.Text.StringBuilder sql = new();
        ScopeClause.AppendProjectAndTenant(sql, "v7", Org);

        sql.ToString().Should().Contain("tenant_id = {tenantId:String} AND tenant_id IN (SELECT tenant_id");
    }

    [Fact]
    public void ScopeParameters_CarryTheSetOnlyWhenThereIsOne()
    {
        ScopeClause.Parameters(new QueryScope { ProjectId = "p", TenantId = "t" })
            .Select(p => p.Key).Should().NotContain("tenantSetAttribute");

        List<KeyValuePair<string, object?>> parameters =
            ScopeClause.Parameters(new QueryScope { ProjectId = "p", TenantId = string.Empty, TenantSet = Org });

        parameters.Should().Contain(new KeyValuePair<string, object?>("tenantSetAttribute", "parent"));
        parameters.Should().Contain(new KeyValuePair<string, object?>("tenantSetValue", "org-1"));
        parameters.Should().Contain(new KeyValuePair<string, object?>("tenantId", string.Empty),
            "a null string parameter has no ClickHouse type");
    }

    [Fact]
    public void GroupByATenantAttribute_IsAcceptedOnlyWhenDeclared()
    {
        Columns().TryResolveGroupBy("tenant.region", out string column).Should().BeTrue();
        column.Should().Be("tenant.region");
        Columns().TryResolveGroupBy("tenant.unknown", out _).Should().BeFalse();
        Columns().TryResolve("tenant.region", out _).Should().BeFalse("a tenant attribute is a group key, not a filter column");
        new QueryColumns(new DimensionRegistry(new TelemetryEngineOptions())).TryResolveGroupBy("tenant.region", out _)
            .Should().BeFalse();
    }

    private static BreakdownRequestResult Breakdown(string groupBy, string? tenantId, TenantSet? set) =>
        BreakdownRequestFactory.Create(
            "p", tenantId, "events", groupBy, null, [], null, null, null, null, null, null, null,
            Columns(), Measures(), 1000, 24, Now, set);

    [Fact]
    public void ABreakdownOverASet_NeedsNoTenant()
    {
        BreakdownRequestResult result = Breakdown("widget_id", null, Org);

        result.Success.Should().BeTrue();
        result.Request.TenantId.Should().BeEmpty();
        result.Request.Scope.TenantSet.Should().Be(Org);
    }

    [Fact]
    public void ABreakdownWithNeitherTenantNorSet_IsForbidden()
    {
        Breakdown("widget_id", null, null).ErrorStatusCode.Should().Be(BreakdownRequestFactory.StatusForbidden);
    }

    [Fact]
    public void ABreakdownByTenantAttribute_MapsEachTenantToItsValue()
    {
        BreakdownRequest request = Breakdown("tenant.region", null, Org).Request;

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = GetBreakdownQuery.BuildQuery(request);

        sql.Should().StartWith("WITH (SELECT (groupArray(tenant_id), groupArray(value)) FROM nealytics_core.tenant_attributes FINAL");
        sql.Should().Contain("transform(tenant_id, tenant_attribute_map.1, tenant_attribute_map.2, '') AS key");
        parameters.Should().Contain(new KeyValuePair<string, object?>("groupAttribute", "region"));
    }

    [Fact]
    public void ASingleTenantBreakdown_BuildsTheSameSqlAsBefore()
    {
        BreakdownRequest request = Breakdown("widget_id", "t", null).Request;

        GetBreakdownQuery.BuildQuery(request).Sql.Should().Be(
            "WITH grouped AS (SELECT ifNull(toString(widget_id), '') AS key, count() AS value FROM "
            + "nealytics_core.global_events WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String} "
            + "AND timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64} "
            + "AND traffic_class = {trafficClass:String} GROUP BY key) SELECT key, value, (SELECT sum(value) FROM grouped) "
            + "AS grand_total, (SELECT count() FROM grouped) AS group_count FROM grouped ORDER BY value DESC LIMIT {limit:Int32}");
    }

    [Fact]
    public void APivotByTenantAttribute_UsesTheSameMapping()
    {
        PivotRequestResult result = PivotRequestFactory.Create(
            "p", null, "tenant.parent", ["events", "sessions"], [], null, null, null, null, null, null, null, null,
            Columns(), Measures(), 1000, 24, Now, Org);

        result.Success.Should().BeTrue();
        GetPivotQuery.BuildQuery(result.Request).Sql.Should().Contain("transform(tenant_id, tenant_attribute_map.1");
    }

    [Fact]
    public void AFunnelOverASet_ScopesThroughMembership()
    {
        FunnelRequestResult result = FunnelRequestFactory.Create(
            "p", null, ["a", "b"], null, null, null, null, null, null, Columns(), Measures(), 1000, 24, Now, Org);

        result.Success.Should().BeTrue();
        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = GetFunnelQuery.BuildQuery(result.Request);
        sql.Should().Contain("tenant_id IN (SELECT tenant_id FROM nealytics_core.tenant_attributes FINAL");
        parameters.Should().Contain(new KeyValuePair<string, object?>("tenantSetValue", "org-1"));
    }

    [Fact]
    public void ATimelineOverASet_ReturnsEachEventsTenant()
    {
        TimelineRequestResult result = TimelineRequestFactory.Create(
            "p", null, null, null, null, null, null, null, null, 100, tenantSet: Org);

        result.Success.Should().BeTrue();
        string sql = GetProjectTimelineQuery.BuildQuery(result.Request).Sql;
        sql.Should().Contain(", tenant_id FROM nealytics_core.global_events");
        sql.Should().Contain("tenant_id IN (SELECT tenant_id");
    }

    [Fact]
    public void TenantAttributeNames_AreValidatedAtBoot()
    {
        Action invalid = () => new TenantAttributeRegistry(new TelemetryEngineOptions
        {
            TenantAttributes = [new TenantAttributeOptions { Name = "Parent-Id" }],
        });
        Action duplicate = () => new TenantAttributeRegistry(new TelemetryEngineOptions
        {
            TenantAttributes = [new TenantAttributeOptions { Name = "parent" }, new TenantAttributeOptions { Name = "parent" }],
        });

        invalid.Should().Throw<InvalidOperationException>().WithMessage("*not valid*");
        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*a second time*");
    }
}

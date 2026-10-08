using FluentAssertions;
using Nealytics.Engine.Features.UpsertTenantAttributes;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public class TenantAttributesRequestFactoryTests
{
    private static readonly TenantAttributeRegistry Registry = new(new TelemetryEngineOptions
    {
        TenantAttributes = [new TenantAttributeOptions { Name = "parent" }, new TenantAttributeOptions { Name = "region" }],
    });

    private static TenantAttributesPayload Payload(params TenantAttributesEntry[] tenants) =>
        new() { ProjectId = "p", Tenants = [.. tenants] };

    private static TenantAttributesEntry Tenant(string id, params (string Name, string? Value)[] attributes) =>
        new() { TenantId = id, Attributes = attributes.ToDictionary(a => a.Name, a => a.Value) };

    [Fact]
    public void EveryAttributeBecomesOneRow_AndNullClearsTheValue()
    {
        TenantAttributesRequestResult result = TenantAttributesRequestFactory.Create(
            Payload(Tenant("v1", ("parent", "org-1"), ("region", null)), Tenant("v2", ("parent", "org-1"))), Registry, 100);

        result.Success.Should().BeTrue();
        result.TenantCount.Should().Be(2);
        result.Rows.Should().HaveCount(3);
        result.Rows.Should().Contain(new TenantAttributeRow { TenantId = "v1", Attribute = "region", Value = "" });
    }

    [Fact]
    public void ATenantListedTwice_IsRefused()
    {
        TenantAttributesRequestFactory.Create(Payload(Tenant("v1", ("parent", "a")), Tenant("v1", ("parent", "b"))), Registry, 100)
            .ErrorMessage.Should().Contain("listed twice");
    }

    [Fact]
    public void AnOversizedBatch_IsRefused()
    {
        TenantAttributesRequestFactory.Create(Payload(Tenant("v1", ("parent", "a")), Tenant("v2", ("parent", "b"))), Registry, 1)
            .ErrorMessage.Should().Contain("At most 1");
    }

    [Fact]
    public void WithoutDeclaredAttributes_NothingIsAccepted()
    {
        TenantAttributesRequestFactory.Create(Payload(Tenant("v1", ("parent", "a"))), new TenantAttributeRegistry(new TelemetryEngineOptions()), 100)
            .ErrorMessage.Should().Contain("declares no tenant attributes");
    }

    [Fact]
    public void AnOverlongValue_IsRefused()
    {
        TenantAttributesRequestFactory.Create(Payload(Tenant("v1", ("parent", new string('x', 257)))), Registry, 100)
            .Success.Should().BeFalse();
    }
}

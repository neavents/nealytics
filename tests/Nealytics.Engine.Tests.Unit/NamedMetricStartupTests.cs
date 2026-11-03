using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Nealytics.Engine.Features.GetNamedMetric;
using Nealytics.Engine.Features.GetSchema;
using Nealytics.Engine.Features.GetSavedReport;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class NamedMetricStartupTests
{
    [Fact]
    public async Task ExistingDeploymentsRegisterAnEmptyRegistryAndTheQuerySlice()
    {
        await using var factory = new NoDatabaseWebFactory();
        Assert.Empty(factory.Services.GetRequiredService<NamedMetricRegistry>().Declared);
        Assert.Empty(factory.Services.GetRequiredService<EventSegmentRegistry>().Declared);
        Assert.Empty(factory.Services.GetRequiredService<SavedReportRegistry>().Declared);
        using var scope = factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GetNamedMetricQuery>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GetSavedReportQuery>());
        Assert.Empty(new SchemaResponse().NamedMetrics);
        Assert.Empty(new SchemaResponse().EventSegments);
        Assert.Empty(new SchemaResponse().SavedReports);
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.Single(
            candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetNamedMetric");
        Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
            response => response.StatusCode == 200 && response.Type == typeof(NamedMetricResponse));
        var report = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.Single(
            candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetSavedReport");
        Assert.Contains(report.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
            response => response.StatusCode == 200 && response.Type == typeof(SavedReportResponse));
    }
}

using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.KafkaIngestion;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;

namespace Nealytics.Engine.Tests.Unit;

public sealed class KafkaConnectorRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledConnectorRegistersNoRuntimeOrConsumer(bool absent)
    {
        var options = KafkaTestSupport.Options();
        if (absent) options.Kafka = null;
        else options.Kafka!.Enabled = false;
        var services = new ServiceCollection();
        services.AddKafkaConnector(options, new ProjectCatalog(options, ProjectContext.Compile("baseline", options)));
        Assert.Empty(services);
    }

    [Fact]
    public void EnabledConnectorResolvesItsOwnSchemaAndBundledNativeConsumer()
    {
        var options = KafkaTestSupport.Options();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<TelemetryEngineOptions>>(Options.Create(options));
        services.AddSingleton<ApiKeyValidator>();
        services.AddKafkaConnector(options, new ProjectCatalog(options, ProjectContext.Compile("baseline", options)));
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<KafkaConnectorSettings>();
        Assert.Equal("p", settings.Policy.PinnedProject);
        Assert.Same(settings, provider.GetRequiredService<KafkaConnectorSettings>());
        Assert.Same(provider.GetRequiredService<KafkaRecordParser>(), provider.GetRequiredService<KafkaRecordParser>());
        var factory = provider.GetRequiredService<Func<IConsumer<Ignore, byte[]>>>();
        using var consumer = factory();
        Assert.NotNull(consumer.Name);
        consumer.Close();
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(KafkaConnector));
    }
}

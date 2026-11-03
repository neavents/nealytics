using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.KafkaIngestion;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Project context hosts")]
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
    public async Task RealHostRegistersTheOptedInWorkerWhileExistingHttpRemainsAvailable()
    {
        var consumer = Substitute.For<IConsumer<Ignore, byte[]>>();
        consumer.Consume(Arg.Any<CancellationToken>()).Returns(call =>
        {
            var token = call.Arg<CancellationToken>();
            token.WaitHandle.WaitOne();
            throw new OperationCanceledException(token);
        });
        await using var host = new ProjectContextHttpTests.Factory(customize: options =>
        {
            options.Kafka = KafkaTestSupport.Options().Kafka;
            options.Kafka!.ProjectKey = "alpha-key";
        }, configure: services =>
        {
            services.RemoveAll<Func<IConsumer<Ignore, byte[]>>>();
            services.AddSingleton<Func<IConsumer<Ignore, byte[]>>>(() => consumer);
        });
        using var client = host.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal("alpha", host.Services.GetRequiredService<KafkaConnectorSettings>().Context.ProjectId);
        Assert.Single(host.Services.GetServices<IHostedService>().OfType<KafkaConnector>());
        await KafkaTestSupport.WaitFor(() => consumer.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "Consume"));
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
        settings.Consumer.Debug = "generic";
        settings.Consumer.BootstrapServers = "127.0.0.1:1";
        using var consumer = factory();
        consumer.Subscribe(settings.Topic);
        Assert.Null(consumer.Consume(TimeSpan.FromMilliseconds(100)));
        Assert.NotNull(consumer.Name);
        consumer.Close();
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(KafkaConnector));
    }
}

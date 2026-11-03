namespace Nealytics.Engine.Features.KafkaIngestion;

using System;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;

internal static class KafkaConnectorRegistration
{
    public static void AddKafkaConnector(this IServiceCollection services, TelemetryEngineOptions options,
        ProjectCatalog projects)
    {
        if (options.Kafka is not { Enabled: true } kafka)
        {
            return;
        }
        services.AddSingleton(provider => KafkaConnectorSettings.Create(
            kafka, provider.GetRequiredService<ApiKeyValidator>(), projects));
        services.AddSingleton(provider => new KafkaRecordParser(
            provider.GetRequiredService<KafkaConnectorSettings>(), options,
            provider.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<Func<IConsumer<Ignore, byte[]>>>(provider => () =>
            new ConsumerBuilder<Ignore, byte[]>(provider.GetRequiredService<KafkaConnectorSettings>().Consumer)
                .SetLogHandler((_, _) => { })
                .Build());
        services.AddHostedService<KafkaConnector>();
    }
}

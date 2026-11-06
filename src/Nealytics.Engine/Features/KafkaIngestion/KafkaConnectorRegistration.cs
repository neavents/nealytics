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
        KafkaConnectorOptions? kafka = options.Kafka;
        if (kafka is null || !kafka.Enabled)
        {
            return;
        }
        if (options.EnableAnalyticsRollupPlanning && options.AnalyticsRollupSourceIsAppendOnly)
        {
            throw new InvalidOperationException("Kafka at-least-once ingestion cannot qualify the append-only analytics rollup assertion.");
        }
        services.AddSingleton(provider => KafkaConnectorSettings.Create(
            kafka, provider.GetRequiredService<ApiKeyValidator>(), projects));
        services.AddSingleton(provider => new KafkaRecordParser(
            provider.GetRequiredService<KafkaConnectorSettings>(), options,
            provider.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<KafkaSourceHealth>();
        services.AddSingleton<Func<IConsumer<Ignore, byte[]>>>(provider =>
        {
            KafkaConnectorSettings settings = provider.GetRequiredService<KafkaConnectorSettings>();
            KafkaSourceHealth health = provider.GetRequiredService<KafkaSourceHealth>();
            return () => new ConsumerBuilder<Ignore, byte[]>(settings.Consumer)
                .SetLogHandler((_, _) => { })
                .SetErrorHandler((_, error) => health.NativeError(error))
                .SetPartitionsAssignedHandler((_, partitions) => health.Assigned(partitions.Count))
                .SetPartitionsRevokedHandler((_, _) => health.Revoked())
                .SetPartitionsLostHandler((_, _) => health.Lost())
                .Build();
        });
        services.AddHostedService<KafkaConnector>();
    }
}

namespace Nealytics.Engine.Features.KafkaIngestion;

using System;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Serialization;

internal sealed class KafkaConnector : BackgroundService
{
    private readonly KafkaConnectorSettings _settings;
    private readonly KafkaRecordParser _parser;
    private readonly DurableIngestDispatcher _dispatcher;
    private readonly Func<IConsumer<Ignore, byte[]>> _createConsumer;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly KafkaSourceHealth _health;

    public KafkaConnector(KafkaConnectorSettings settings, KafkaRecordParser parser,
        DurableIngestDispatcher dispatcher, Func<IConsumer<Ignore, byte[]>> createConsumer,
        IHostApplicationLifetime lifetime, KafkaSourceHealth health)
    {
        _settings = settings;
        _parser = parser;
        _dispatcher = dispatcher;
        _createConsumer = createConsumer;
        _lifetime = lifetime;
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken, _lifetime.ApplicationStopping);
        CancellationToken cancellationToken = shutdown.Token;
        string stage = "create";
        int partition = -1;
        long offset = -1;
        try
        {
            using IConsumer<Ignore, byte[]> consumer = _createConsumer();
            try
            {
                stage = "subscribe";
                consumer.Subscribe(_settings.Topic);
                _health.Subscribed();
                while (!cancellationToken.IsCancellationRequested)
                {
                    stage = "consume";
                    ConsumeResult<Ignore, byte[]> record = consumer.Consume(cancellationToken);
                    partition = record.Partition.Value;
                    offset = record.Offset.Value;
                    stage = "validate";
                    GlobalTelemetryPayload payload = _parser.Parse(record.Message.Value, DateTime.UtcNow);
                    stage = "persist-and-dispatch";
                    await _dispatcher.AppendAsync(payload, cancellationToken);
                    stage = "commit";
                    consumer.Commit([new TopicPartitionOffset(record.TopicPartition, record.Offset + 1)]);
                    _health.Confirmed();
                }
                stage = "close";
            }
            finally
            {
                consumer.Close();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _lifetime.ApplicationStopping.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _health.Failed();
            throw new InvalidOperationException($"Kafka connector stopped during {stage} on topic {_settings.Topic}, partition {partition}, offset {offset}. No unacknowledged record was skipped; replay may duplicate durable events.");
        }
        finally
        {
            _health.Stopped();
        }
    }
}

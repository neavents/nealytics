using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nealytics.Engine.Features.KafkaIngestion;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Octonica.ClickHouseClient;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class KafkaConnectorIntegrationTests : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly IContainer _broker = new ContainerBuilder("apache/kafka:4.1.1@sha256:0bc1bb2478f45b6cea78864df86acdc11e8df2c5172477819a4d12942cbe5d40")
        .WithLabel("nealytics.scope", "kafka-integration")
        .WithPortBinding(9092, true)
        .WithEntrypoint("/bin/sh", "-c")
        .WithCommand("sleep infinity")
        .WithEnvironment("KAFKA_NODE_ID", "1")
        .WithEnvironment("KAFKA_PROCESS_ROLES", "broker,controller")
        .WithEnvironment("KAFKA_LISTENERS", "PLAINTEXT://0.0.0.0:9092,CONTROLLER://0.0.0.0:9093")
        .WithEnvironment("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT")
        .WithEnvironment("KAFKA_CONTROLLER_LISTENER_NAMES", "CONTROLLER")
        .WithEnvironment("KAFKA_INTER_BROKER_LISTENER_NAME", "PLAINTEXT")
        .WithEnvironment("KAFKA_CONTROLLER_QUORUM_VOTERS", "1@localhost:9093")
        .WithEnvironment("KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR", "1")
        .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR", "1")
        .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_MIN_ISR", "1")
        .WithEnvironment("KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS", "0")
        .WithEnvironment("KAFKA_HEAP_OPTS", "-Xms256m -Xmx512m")
        .Build();
    private string Servers => _broker.Hostname + ":" + _broker.GetMappedPublicPort(9092);
    private string Project => "kafka_" + _suffix;
    private string Topic => "events_" + _suffix;
    private string Group => "group_" + _suffix;

    public async Task InitializeAsync()
    {
        await _broker.StartAsync();
        string command = "export KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://" + Servers
            + "; /etc/kafka/docker/run >/tmp/kafka-start.log 2>&1 &";
        Assert.Equal(0, (await _broker.ExecAsync(["/bin/sh", "-c", command])).ExitCode);
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Servers }).Build();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        while (true)
        {
            try
            {
                if (admin.GetMetadata(TimeSpan.FromSeconds(1)).Brokers.Count > 0) break;
            }
            catch (KafkaException) { }
            await Task.Delay(100, timeout.Token);
        }
        await admin.CreateTopicsAsync([new TopicSpecification { Name = Topic, NumPartitions = 2, ReplicationFactor = 1 }]);
    }

    [Theory]
    [InlineData("managed", false)]
    [InlineData("custom", false)]
    [InlineData("managed", true)]
    [InlineData("custom", true)]
    public async Task RealKafkaOffsetsSurviveRestartAndPoisonInputCannotAdvanceThePrefix(string path, bool mapped)
    {
        string group = Group + path + mapped;
        string project = Project + path + mapped;
        using var producer = new ProducerBuilder<Null, byte[]>(new ProducerConfig { BootstrapServers = Servers }).Build();
        var first = await producer.ProduceAsync(new TopicPartition(Topic, 0), new() { Value = Event(project, 1, mapped) });
        await using (var host = new Factory(Servers, Topic, group, project, path, mapped))
        {
            using var client = host.CreateClient();
            await WaitForOffset(group, 0, first.Offset + 1);
            await WaitForCount(project, 1);
            Assert.False(host.Services.GetServices<IHostedService>().OfType<KafkaConnector>().Single().ExecuteTask!.IsCompleted);
        }
        var second = await producer.ProduceAsync(new TopicPartition(Topic, 1), new() { Value = Event(project, 2, mapped) });
        var poison = await producer.ProduceAsync(new TopicPartition(Topic, 1), new() { Value = Encoding.UTF8.GetBytes("{invalid}") });
        await producer.ProduceAsync(new TopicPartition(Topic, 1), new() { Value = Event(project, 3, mapped) });
        await using (var restarted = new Factory(Servers, Topic, group, project, path, mapped))
        {
            using var client = restarted.CreateClient();
            var connector = restarted.Services.GetServices<IHostedService>().OfType<KafkaConnector>().Single();
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => connector.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Contains("during validate", failure.Message);
            Assert.Contains("partition 1, offset " + poison.Offset.Value, failure.Message);
            Assert.True(restarted.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
            Assert.Equal(second.Offset + 1, Committed(group, 1));
            Assert.Equal(first.Offset + 1, Committed(group, 0));
        }
        await WaitForCount(project, 2);
        Assert.Equal(2, await Count(project));
    }

    private byte[] Event(string project, int sequence, bool mapped) => mapped ? JsonSerializer.SerializeToUtf8Bytes(new
    {
        source = new { id = Guid.NewGuid(), time = DateTime.UtcNow, tenant = "t" }
    }) : JsonSerializer.SerializeToUtf8Bytes(new GlobalTelemetryPayload
    {
        EventId = Guid.NewGuid(), ProjectId = project, TenantId = "t", EventType = "action",
        Timestamp = DateTime.UtcNow, Seq = (uint)sequence
    }, TelemetryAotContext.Default.GlobalTelemetryPayload);

    private Offset Committed(string group, int partition)
    {
        using var consumer = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
        {
            BootstrapServers = Servers, GroupId = group, EnableAutoCommit = false, EnableAutoOffsetStore = false
        }).Build();
        return consumer.Committed([new TopicPartition(Topic, partition)], TimeSpan.FromSeconds(5)).Single().Offset;
    }

    private async Task WaitForOffset(string group, int partition, Offset expected)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                if (Committed(group, partition) == expected) return;
            }
            catch (KafkaException exception) when (exception.Error.Code is ErrorCode.NotCoordinatorForGroup or ErrorCode.GroupCoordinatorNotAvailable)
            {
            }
            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task WaitForCount(string project, long expected)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (await Count(project) != expected) await Task.Delay(50, timeout.Token);
    }

    private static async Task<long> Count(string project)
    {
        await using var connection = new ClickHouseConnection(ClickHouseTestSupport.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count() FROM nealytics_core.global_events WHERE project_id = {project:String}";
        command.Parameters.AddWithValue("project", project);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task DisposeAsync()
    {
        await _broker.DisposeAsync();
        await ClickHouseTestSupport.DeleteProjectsAsync(Project + "managedFalse", Project + "customFalse", Project + "managedTrue", Project + "customTrue");
    }

    private sealed class Factory : TestWebApplicationFactory
    {
        private readonly string _schema = Path.Combine(Path.GetTempPath(), "kafka-schema-" + Guid.NewGuid().ToString("N") + ".json");
        private readonly string? _previousSchema;
        private readonly string _wal = Path.Combine(Path.GetTempPath(), "kafka-wal-" + Guid.NewGuid().ToString("N"));
        public Factory(string servers, string topic, string group, string project, string path, bool mapped)
        {
            _previousSchema = Environment.GetEnvironmentVariable("TelemetryEngine__SchemaFile");
            File.WriteAllText(_schema, JsonSerializer.Serialize(new
            {
                TelemetryEngine = new
                {
                    IngestionExecutionPath = path,
                    IngestionKeys = new[] { new { Key = "integration-source-key", Scope = "server", ProjectId = project, EventTypes = "action" } },
                    Kafka = new { Enabled = true, BootstrapServers = servers, Topic = topic, GroupId = group,
                        ProjectKey = "integration-source-key", SecurityProtocol = "plaintext", InitialOffset = "earliest",
                        SourceMapping = mapped ? new SourceMappingOptions
                        {
                            Fields = new()
                            {
                                ["eventId"] = new() { Path = ["source", "id"] },
                                ["timestamp"] = new() { Path = ["source", "time"] },
                                ["projectId"] = new() { Default = project },
                                ["tenantId"] = new() { Path = ["source", "tenant"] },
                                ["eventType"] = new() { Default = "action" }
                            }
                        } : null }
                }
            }));
            Environment.SetEnvironmentVariable("TelemetryEngine__SchemaFile", _schema);
            Environment.SetEnvironmentVariable("TelemetryEngine__WriteAheadLogDirectory", _wal);
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Environment.SetEnvironmentVariable("TelemetryEngine__SchemaFile", _previousSchema);
            File.Delete(_schema);
            if (Directory.Exists(_wal)) Directory.Delete(_wal, true);
        }
    }
}

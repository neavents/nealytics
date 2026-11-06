using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.KafkaIngestion;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class KafkaSecurityIntegrationTests : IClassFixture<KafkaSecurityIntegrationTests.Fixture>
{
    private readonly KafkaSecurityBroker _broker;
    private readonly string _topic;

    public KafkaSecurityIntegrationTests(Fixture fixture) { _broker = fixture.Broker; _topic = fixture.Topic; }

    private ConsumerConfig Consumer(string mode, string group, string? fault = null)
    {
        var options = new TelemetryEngineOptions
        {
            IngestionKeys = [new() { Key = "secure-key", Scope = "server", ProjectId = "p" }],
            Kafka = _broker.Options(mode, group, "secure-key", fault)
        };
        options.Kafka.Topic = _topic;
        var projects = new ProjectCatalog(options, ProjectContext.Compile("baseline", options));
        return KafkaConnectorSettings.Create(options.Kafka,
            new ApiKeyValidator(Microsoft.Extensions.Options.Options.Create(options), NullLogger<ApiKeyValidator>.Instance), projects).Consumer;
    }

    [Theory]
    [InlineData("ssl")]
    [InlineData("plain")]
    [InlineData("scram-sha-256")]
    [InlineData("scram-sha-512")]
    public void NativeClientAuthenticatesAllSupportedSecureModes(string mode)
    {
        var errors = new ConcurrentQueue<Error>();
        using var consumer = new ConsumerBuilder<Ignore, byte[]>(Consumer(mode, Guid.NewGuid().ToString("N")))
            .SetErrorHandler((_, error) => errors.Enqueue(error)).SetLogHandler((_, _) => { }).Build();
        consumer.Subscribe(_topic);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var record = consumer.Consume(deadline.Token);
        Assert.Equal("secure-value"u8.ToArray(), record.Message.Value);
        consumer.Commit([new TopicPartitionOffset(record.TopicPartition, record.Offset + 1)]);
        Assert.Equal(record.Offset + 1, consumer.Committed([record.TopicPartition], TimeSpan.FromSeconds(5)).Single().Offset);
        Assert.DoesNotContain(errors, error => error.Code is ErrorCode.Local_Ssl or ErrorCode.Local_Authentication);
        consumer.Close();
    }

    [Theory]
    [InlineData("ssl", "trust", ErrorCode.Local_Ssl)]
    [InlineData("ssl", "hostname", ErrorCode.Local_Ssl)]
    [InlineData("plain", "password", ErrorCode.Local_Authentication)]
    [InlineData("scram-sha-256", "password", ErrorCode.Local_Authentication)]
    [InlineData("scram-sha-512", "password", ErrorCode.Local_Authentication)]
    public void NativeClientRejectsTrustHostnameAndCredentialFailuresBeforeAnyRecord(string mode, string fault, ErrorCode expected)
    {
        var errors = new ConcurrentQueue<Error>();
        string group = Guid.NewGuid().ToString("N");
        using var observer = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
        { BootstrapServers = _broker.AdminServers, GroupId = group, EnableAutoCommit = false, EnableAutoOffsetStore = false }).Build();
        observer.Assign(new TopicPartition(_topic, 0));
        observer.Commit([new TopicPartitionOffset(_topic, 0, 0)]);
        using var consumer = new ConsumerBuilder<Ignore, byte[]>(Consumer(mode, group, fault))
            .SetErrorHandler((_, error) => errors.Enqueue(error)).SetLogHandler((_, _) => { }).Build();
        consumer.Subscribe(_topic);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!errors.Any(error => error.Code == expected))
        {
            deadline.Token.ThrowIfCancellationRequested();
            Assert.Null(consumer.Consume(TimeSpan.FromMilliseconds(100)));
        }
        Assert.Contains(errors, error => error.Code == expected);
        consumer.Close();
        Assert.Equal(new Offset(0), observer.Committed([new TopicPartition(_topic, 0)], TimeSpan.FromSeconds(5)).Single().Offset);
        observer.Close();
    }

    [Theory]
    [InlineData("ssl", "managed", false)]
    [InlineData("ssl", "custom", false)]
    [InlineData("ssl", "managed", true)]
    [InlineData("ssl", "custom", true)]
    [InlineData("plain", "managed", false)]
    [InlineData("plain", "custom", false)]
    [InlineData("plain", "managed", true)]
    [InlineData("plain", "custom", true)]
    [InlineData("scram-sha-256", "managed", false)]
    [InlineData("scram-sha-256", "custom", false)]
    [InlineData("scram-sha-256", "managed", true)]
    [InlineData("scram-sha-256", "custom", true)]
    [InlineData("scram-sha-512", "managed", false)]
    [InlineData("scram-sha-512", "custom", false)]
    [InlineData("scram-sha-512", "managed", true)]
    [InlineData("scram-sha-512", "custom", true)]
    public async Task EncryptedRecordsPreserveDurableOffsetsAcrossRestartAndStopAtPoison(string mode, string execution, bool mapped)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string topic = "source_" + suffix; string group = "group_" + suffix; string project = "e8_" + suffix;
        string wal = Path.Combine(Path.GetTempPath(), "nealytics-security-wal-" + suffix);
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _broker.AdminServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
        using var producer = new ProducerBuilder<Null, byte[]>(new ProducerConfig { BootstrapServers = _broker.AdminServers }).Build();
        byte[] Event() => JsonSerializer.SerializeToUtf8Bytes(mapped ? (object)new
        { source = new { id = Guid.NewGuid(), time = DateTime.UtcNow, tenant = "t" } } : new
        { eventId = Guid.NewGuid(), timestamp = DateTime.UtcNow, projectId = project, tenantId = "t", eventType = "action" });
        Offset Committed()
        {
            using var reader = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
            { BootstrapServers = _broker.AdminServers, GroupId = group, EnableAutoCommit = false }).Build();
            return reader.Committed([new TopicPartition(topic, 0)], TimeSpan.FromSeconds(5)).Single().Offset;
        }
        async Task WaitFor(Offset offset, long count)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                try { if (Committed() == offset && await ClickHouseTestSupport.CountAsync(project) == count) return; }
                catch (KafkaException error) when (error.Error.Code is ErrorCode.NotCoordinatorForGroup or ErrorCode.GroupCoordinatorNotAvailable) { }
                await Task.Delay(50, deadline.Token);
            }
        }
        KafkaConnectorOptions Options()
        {
            var options = _broker.Options(mode, group, "secure-source-key"); options.Topic = topic;
            if (mapped) options.SourceMapping = new SourceMappingOptions
            {
                Fields = new()
                {
                    ["eventId"] = new() { Path = ["source", "id"] },
                    ["timestamp"] = new() { Path = ["source", "time"] },
                    ["tenantId"] = new() { Path = ["source", "tenant"] },
                    ["projectId"] = new() { Default = project }, ["eventType"] = new() { Default = "action" }
                }
            };
            return options;
        }
        try
        {
            var first = await producer.ProduceAsync(topic, new() { Value = Event() });
            await using (var host = new SecurityFactory(Options(), project, execution, wal))
            {
                using var client = host.CreateClient();
                await WaitFor(first.Offset + 1, 1);
                Assert.False(host.Services.GetServices<IHostedService>().OfType<KafkaConnector>().Single().ExecuteTask!.IsCompleted);
                using var observed = await client.GetAsync("/api/v1/telemetry/sources/kafka/default?k=secure-source-key");
                Assert.Equal(HttpStatusCode.OK, observed.StatusCode);
                Assert.Equal("assigned", (await observed.Content.ReadFromJsonAsync<KafkaSourceHealthResponse>())!.State);
            }
            var second = await producer.ProduceAsync(topic, new() { Value = Event() });
            var poison = await producer.ProduceAsync(topic, new() { Value = "{private-secure-payload}"u8.ToArray() });
            await producer.ProduceAsync(topic, new() { Value = Event() });
            await using (var restarted = new SecurityFactory(Options(), project, execution, wal))
            {
                using var client = restarted.CreateClient();
                var connector = restarted.Services.GetServices<IHostedService>().OfType<KafkaConnector>().Single();
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => connector.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.Contains("during validate on topic " + topic + ", partition 0, offset " + poison.Offset.Value, failure.Message);
                Assert.Null(failure.InnerException); Assert.DoesNotContain("private-secure-payload", failure.Message);
                Assert.DoesNotContain(_broker.Password, failure.Message);
                Assert.Equal("failed", restarted.Services.GetRequiredService<KafkaSourceHealth>().Snapshot().State);
                Assert.True(restarted.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
                await WaitFor(second.Offset + 1, 2);
            }
            Assert.Equal(second.Offset + 1, Committed());
            Assert.Equal(2, await ClickHouseTestSupport.CountAsync(project));
        }
        finally
        {
            await ClickHouseTestSupport.DeleteProjectsAsync(project);
            if (Directory.Exists(wal)) Directory.Delete(wal, true);
        }
    }

    [Theory]
    [InlineData("ssl", null, null)]
    [InlineData("plain", null, null)]
    [InlineData("scram-sha-256", null, null)]
    [InlineData("scram-sha-512", null, null)]
    [InlineData("ssl", "trust", "tls_rejected")]
    [InlineData("ssl", "hostname", "tls_rejected")]
    [InlineData("plain", "password", "authentication_failed")]
    [InlineData("scram-sha-256", "password", "authentication_failed")]
    [InlineData("scram-sha-512", "password", "authentication_failed")]
    public async Task SourceHealthDistinguishesIdleAssignmentsFromNativeSecurityFailures(string mode, string? fault, string? issue)
    {
        string suffix = Guid.NewGuid().ToString("N"); string project = "d3_" + suffix;
        string wal = Path.Combine(Path.GetTempPath(), "nealytics-source-health-" + suffix);
        var options = _broker.Options(mode, "health_" + suffix, "secure-source-key", fault); options.Topic = "health_" + suffix;
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _broker.AdminServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = options.Topic, NumPartitions = 1, ReplicationFactor = 1 }]);
        try
        {
            await using var host = new SecurityFactory(options, project, "custom", wal);
            using var client = host.CreateClient();
            var status = await WaitForHealth(client, issue is null ? "assigned" : "unavailable", issue);
            Assert.Equal("default", status.Name); Assert.Equal(DateTimeKind.Utc, status.ChangedAt.Kind);
            Assert.Equal(0, host.Services.GetRequiredService<Nealytics.Engine.Infrastructure.Storage.WriteAheadLogger>().UncommittedRecordCount);
            Assert.Equal(0, await ClickHouseTestSupport.CountAsync(project));
            Assert.False(host.Services.GetServices<IHostedService>().OfType<KafkaConnector>().Single().ExecuteTask!.IsCompleted);
            using var ready = await client.GetAsync("/ready"); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }
        finally
        {
            await ClickHouseTestSupport.DeleteProjectsAsync(project);
            if (Directory.Exists(wal)) Directory.Delete(wal, true);
        }
    }

    [Theory]
    [InlineData("ssl", "managed")]
    [InlineData("scram-sha-512", "custom")]
    public async Task BrokerOutageLosesAssignmentAndRecoversHealthWithoutChangingOffsetOwnership(string mode, string execution)
    {
        string suffix = Guid.NewGuid().ToString("N"); string project = "d3_" + suffix;
        string wal = Path.Combine(Path.GetTempPath(), "nealytics-source-recovery-" + suffix);
        var options = _broker.Options(mode, "health_" + suffix, "secure-source-key"); options.Topic = "health_" + suffix;
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _broker.AdminServers }).Build())
            await admin.CreateTopicsAsync([new TopicSpecification { Name = options.Topic, NumPartitions = 1, ReplicationFactor = 1 }]);
        bool stopped = false;
        try
        {
            await using var host = new SecurityFactory(options, project, execution, wal);
            using var client = host.CreateClient(); await WaitForHealth(client, "assigned");
            await _broker.StopServerAsync(); stopped = true;
            await WaitForHealth(client, "unavailable");
            await WaitForHealth(client, "unavailable", "assignment_lost");
            Assert.False(host.Services.GetServices<IHostedService>().OfType<KafkaConnector>().Single().ExecuteTask!.IsCompleted);
            Assert.Equal(0, await ClickHouseTestSupport.CountAsync(project));
            using var ready = await client.GetAsync("/ready"); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            await _broker.RestartServerAsync(); stopped = false;
            using var producer = new ProducerBuilder<Null, byte[]>(new ProducerConfig { BootstrapServers = _broker.AdminServers }).Build();
            var record = await producer.ProduceAsync(options.Topic, new() { Value = JsonSerializer.SerializeToUtf8Bytes(new
            { eventId = Guid.NewGuid(), timestamp = DateTime.UtcNow, projectId = project, tenantId = "t", eventType = "action" }) });
            await WaitForHealth(client, "assigned");
            using var observer = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
            { BootstrapServers = _broker.AdminServers, GroupId = options.GroupId, EnableAutoCommit = false }).Build();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (observer.Committed([record.TopicPartition], TimeSpan.FromSeconds(2)).Single().Offset != record.Offset + 1
                || await ClickHouseTestSupport.CountAsync(project) != 1) await Task.Delay(100, deadline.Token);
            observer.Close(); Assert.Equal(1, await ClickHouseTestSupport.CountAsync(project));
        }
        finally
        {
            if (stopped) await _broker.RestartServerAsync();
            await ClickHouseTestSupport.DeleteProjectsAsync(project);
            if (Directory.Exists(wal)) Directory.Delete(wal, true);
        }
    }

    private static async Task<KafkaSourceHealthResponse> WaitForHealth(HttpClient client, string state, string? issue = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            using var response = await client.GetAsync("/api/v1/telemetry/sources/kafka/default?k=secure-source-key", deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            var health = (await response.Content.ReadFromJsonAsync<KafkaSourceHealthResponse>(deadline.Token))!;
            if (health.State == state && (issue is null || health.Issue == issue)) return health;
            await Task.Delay(100, deadline.Token);
        }
    }

    private sealed class SecurityFactory : TestWebApplicationFactory
    {
        private readonly string _schema = Path.Combine(Path.GetTempPath(), "nealytics-security-schema-" + Guid.NewGuid().ToString("N") + ".json");
        private readonly string? _previousSchema;
        public SecurityFactory(KafkaConnectorOptions kafka, string project, string execution, string wal)
        {
            _previousSchema = Environment.GetEnvironmentVariable("TelemetryEngine__SchemaFile");
            File.WriteAllText(_schema, JsonSerializer.Serialize(new
            {
                TelemetryEngine = new
                {
                    IngestionExecutionPath = execution,
                    IngestionKeys = new[] { new { Key = "secure-source-key", Scope = "server", ProjectId = project, EventTypes = "action" } },
                    Kafka = kafka
                }
            }));
            Environment.SetEnvironmentVariable("TelemetryEngine__SchemaFile", _schema);
            Environment.SetEnvironmentVariable("TelemetryEngine__WriteAheadLogDirectory", wal);
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Environment.SetEnvironmentVariable("TelemetryEngine__SchemaFile", _previousSchema);
            File.Delete(_schema);
        }
    }

    public sealed class Fixture : IAsyncLifetime
    {
        internal KafkaSecurityBroker Broker { get; } = new();
        public string Topic { get; } = "secure_" + Guid.NewGuid().ToString("N");
        public async Task InitializeAsync()
        {
            await Broker.StartAsync();
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Broker.AdminServers }).Build();
            await admin.CreateTopicsAsync([new TopicSpecification { Name = Topic, NumPartitions = 1, ReplicationFactor = 1 }]);
            using var producer = new ProducerBuilder<Null, byte[]>(new ProducerConfig { BootstrapServers = Broker.AdminServers }).Build();
            await producer.ProduceAsync(Topic, new() { Value = "secure-value"u8.ToArray() });
        }
        public Task DisposeAsync() => Broker.DisposeAsync().AsTask();
    }
}

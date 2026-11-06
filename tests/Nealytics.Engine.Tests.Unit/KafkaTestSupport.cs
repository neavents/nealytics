using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Features.KafkaIngestion;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;
using NSubstitute;

namespace Nealytics.Engine.Tests.Unit;

internal static class KafkaTestSupport
{
    public const string Identity = "11111111-1111-1111-1111-111111111111";
    public static TelemetryEngineOptions Options() => new()
    {
        IngestionKeys = [new() { Key = "secret-key", Scope = "server", ProjectId = "p", EventTypes = "action" }],
        Dimensions = [new() { Name = "tag" }],
        Measures = [new() { Name = "amount", Type = "Float64" }],
        Kafka = new() { Enabled = true, BootstrapServers = "localhost:9092", Topic = "events", GroupId = "test-group", ProjectKey = "secret-key", SecurityProtocol = "plaintext" }
    };

    public static KafkaConnectorSettings Settings(TelemetryEngineOptions? options = null)
    {
        options ??= Options();
        return KafkaConnectorSettings.Create(options.Kafka!,
            new ApiKeyValidator(Microsoft.Extensions.Options.Options.Create(options), NullLogger<ApiKeyValidator>.Instance),
            new ProjectCatalog(options, ProjectContext.Compile("baseline", options)));
    }

    public static string Event(string extra = "") =>
        "{\"eventId\":\"" + Identity + "\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"projectId\":\"p\",\"tenantId\":\"t\",\"eventType\":\"action\"" + extra + "}";

    public static ConsumeResult<Ignore, byte[]> Record(string? json = null, int partition = 2, long offset = 41) => new()
    {
        Topic = "events", Partition = partition, Offset = offset,
        Message = new() { Value = Encoding.UTF8.GetBytes(json ?? Event()) }
    };

    public static async Task WaitFor(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    internal sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-kafka-" + Guid.NewGuid().ToString("N"));
        public Harness(string path = "managed", int capacity = 8, IWalFile? file = null, Func<IConsumer<Ignore, byte[]>>? factory = null)
        {
            var options = Options();
            options.WriteAheadLogDirectory = _directory;
            options.MemoryChannelCapacity = capacity;
            options.IngestionExecutionPath = path;
            var configured = Microsoft.Extensions.Options.Options.Create(options);
            Wal = file is null ? new WriteAheadLogger(configured) : new WriteAheadLogger(configured, file);
            Broker = new TelemetryChannelBroker(configured);
            var lifetime = Substitute.For<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Returns(Stopping.Token);
            var dispatcher = new DurableIngestDispatcher(Wal, Broker, new IngestionRequestReader(configured), lifetime);
            Settings = KafkaTestSupport.Settings(options);
            Parser = new KafkaRecordParser(Settings, options, NullLoggerFactory.Instance);
            Consumer = Substitute.For<IConsumer<Ignore, byte[]>>();
            Consumer.Consume(Arg.Any<CancellationToken>()).Returns(call =>
            {
                var token = call.Arg<CancellationToken>();
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("test consumer cancellation did not propagate");
            });
            Connector = new KafkaConnector(Settings, Parser, dispatcher, factory ?? (() => Consumer), lifetime, Health);
        }
        public KafkaConnectorSettings Settings { get; }
        public KafkaRecordParser Parser { get; }
        public KafkaSourceHealth Health { get; } = new();
        public IConsumer<Ignore, byte[]> Consumer { get; }
        public KafkaConnector Connector { get; private set; }
        public WriteAheadLogger Wal { get; }
        public TelemetryChannelBroker Broker { get; }
        public CancellationTokenSource Stopping { get; } = new();
        public string WalPath => Path.Combine(_directory, "telemetry_wal.log");
        public async Task Start() => await Connector.StartAsync(Stopping.Token);
        public async Task Stop()
        {
            Stopping.Cancel();
            await Connector.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        public async ValueTask DisposeAsync()
        {
            await Stop();
            Connector.Dispose();
            await Wal.DisposeAsync();
            Stopping.Dispose();
            Directory.Delete(_directory, true);
        }
    }

    internal sealed class FaultFile(bool writeFailure) : IWalFile
    {
        public ValueTask AppendAsync(ReadOnlyMemory<byte> record) => writeFailure
            ? ValueTask.FromException(new IOException("secret storage detail")) : ValueTask.CompletedTask;
        public ValueTask FlushAsync() => ValueTask.FromException(new IOException("secret storage detail"));
        public ValueTask TruncateAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

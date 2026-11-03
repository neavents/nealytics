namespace Nealytics.Engine.Features.KafkaIngestion;

using System;
using Confluent.Kafka;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;

internal sealed class KafkaConnectorSettings
{
    private KafkaConnectorSettings(ConsumerConfig consumer, string topic, int maxMessageBytes,
        IngestionKeyPolicy policy, ProjectContext context)
    {
        Consumer = consumer;
        Topic = topic;
        MaxMessageBytes = maxMessageBytes;
        Policy = policy;
        Context = context;
    }

    public ConsumerConfig Consumer { get; }
    public string Topic { get; }
    public int MaxMessageBytes { get; }
    public IngestionKeyPolicy Policy { get; }
    public ProjectContext Context { get; }

    public static KafkaConnectorSettings Create(KafkaConnectorOptions options, ApiKeyValidator keys,
        ProjectCatalog projects)
    {
        if (string.IsNullOrWhiteSpace(options.BootstrapServers) || options.BootstrapServers.Length > 4_096
            || !ValidName(options.Topic, 249) || options.Topic is "." or ".."
            || !ValidName(options.GroupId, 256))
        {
            throw new InvalidOperationException("Kafka requires bootstrap servers, a literal topic and a group identifier.");
        }
        if (options.MaxMessageBytes is < 1_024 or > 1_048_576
            || options.QueueBufferKiB is < 1 or > 65_536
            || (long)options.QueueBufferKiB * 1_024 < options.MaxMessageBytes
            || options.MaxPollIntervalMs is < 60_000 or > 900_000)
        {
            throw new InvalidOperationException("Kafka message, queue or poll limits are outside supported bounds.");
        }
        if (!keys.TryResolve(options.ProjectKey, out IngestionKeyPolicy? policy)
            || !policy.IsServer || string.IsNullOrEmpty(policy.PinnedProject)
            || !policy.PermitsProject(policy.PinnedProject))
        {
            throw new InvalidOperationException("Kafka requires a configured server ingestion key pinned to one permitted project.");
        }
        ProjectContext context = projects.Resolve(policy.PinnedProject)
            ?? throw new InvalidOperationException("Kafka project has no configured schema context.");
        AutoOffsetReset initialOffset = options.InitialOffset switch
        {
            "earliest" => AutoOffsetReset.Earliest,
            "latest" => AutoOffsetReset.Latest,
            "error" => AutoOffsetReset.Error,
            _ => throw new InvalidOperationException("Kafka InitialOffset must be earliest, latest or error.")
        };
        SecurityProtocol security = options.SecurityProtocol switch
        {
            "plaintext" => SecurityProtocol.Plaintext,
            "ssl" => SecurityProtocol.Ssl,
            "sasl_ssl" => SecurityProtocol.SaslSsl,
            _ => throw new InvalidOperationException("Kafka SecurityProtocol must be plaintext, ssl or sasl_ssl.")
        };
        ConsumerConfig consumer = new()
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = options.GroupId,
            ClientId = "nealytics",
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            EnablePartitionEof = false,
            AllowAutoCreateTopics = false,
            AutoOffsetReset = initialOffset,
            IsolationLevel = IsolationLevel.ReadCommitted,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.Range,
            MaxPollIntervalMs = options.MaxPollIntervalMs,
            SessionTimeoutMs = 10_000,
            SocketTimeoutMs = 10_000,
            FetchWaitMaxMs = 500,
            MessageMaxBytes = options.MaxMessageBytes,
            FetchMaxBytes = options.MaxMessageBytes,
            MaxPartitionFetchBytes = options.MaxMessageBytes,
            ReceiveMessageMaxBytes = options.QueueBufferKiB * 1_024 + 512,
            QueuedMaxMessagesKbytes = options.QueueBufferKiB,
            QueuedMinMessages = 1,
            SecurityProtocol = security,
            SslCaLocation = options.SslCaLocation,
            EnableSslCertificateVerification = true,
            SslEndpointIdentificationAlgorithm = SslEndpointIdentificationAlgorithm.Https
        };
        if (security == SecurityProtocol.SaslSsl)
        {
            if (string.IsNullOrWhiteSpace(options.SaslUsername) || string.IsNullOrEmpty(options.SaslPassword))
            {
                throw new InvalidOperationException("Kafka SASL requires a username and password.");
            }
            consumer.SaslMechanism = options.SaslMechanism switch
            {
                "plain" => SaslMechanism.Plain,
                "scram-sha-256" => SaslMechanism.ScramSha256,
                "scram-sha-512" => SaslMechanism.ScramSha512,
                _ => throw new InvalidOperationException("Kafka SASL mechanism must be plain, scram-sha-256 or scram-sha-512.")
            };
            consumer.SaslUsername = options.SaslUsername;
            consumer.SaslPassword = options.SaslPassword;
        }
        return new KafkaConnectorSettings(consumer, options.Topic, options.MaxMessageBytes, policy, context);
    }

    private static bool ValidName(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }
        return true;
    }
}

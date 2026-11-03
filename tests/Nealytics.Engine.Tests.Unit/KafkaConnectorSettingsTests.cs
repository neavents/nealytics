using Confluent.Kafka;
using Nealytics.Engine.Features.KafkaIngestion;
using Nealytics.Engine.Infrastructure.Configuration;
using Xunit;

namespace Nealytics.Engine.Tests.Unit;

public sealed class KafkaConnectorSettingsTests
{
    [Fact]
    public void DefaultsRequireExplicitOptInAndSecureTransport()
    {
        var engine = new TelemetryEngineOptions();
        Assert.Null(engine.Kafka);
        var options = new KafkaConnectorOptions();
        Assert.False(options.Enabled);
        Assert.Equal("", options.BootstrapServers);
        Assert.Equal("", options.Topic);
        Assert.Equal("", options.GroupId);
        Assert.Equal("", options.ProjectKey);
        Assert.Equal("error", options.InitialOffset);
        Assert.Equal("ssl", options.SecurityProtocol);
        Assert.Equal("plain", options.SaslMechanism);
        Assert.Equal("", options.SaslUsername);
        Assert.Equal("", options.SaslPassword);
        Assert.Null(options.SslCaLocation);
        Assert.Equal(65_536, options.MaxMessageBytes);
        Assert.Equal(1_024, options.QueueBufferKiB);
        Assert.Equal(300_000, options.MaxPollIntervalMs);
    }

    [Fact]
    public void ConsumerOwnsNoAutomaticOffsetsAndDoesNotCreateTopics()
    {
        var settings = KafkaTestSupport.Settings();
        var config = settings.Consumer;
        Assert.Equal("events", settings.Topic);
        Assert.Equal(65_536, settings.MaxMessageBytes);
        Assert.Equal("p", settings.Policy.PinnedProject);
        Assert.Equal("baseline", settings.Context.ProjectId);
        Assert.Equal("localhost:9092", config.BootstrapServers);
        Assert.Equal("test-group", config.GroupId);
        Assert.Equal("nealytics", config.ClientId);
        Assert.False(config.EnableAutoCommit);
        Assert.False(config.EnableAutoOffsetStore);
        Assert.False(config.EnablePartitionEof);
        Assert.False(config.AllowAutoCreateTopics);
        Assert.Equal(AutoOffsetReset.Error, config.AutoOffsetReset);
        Assert.Equal(IsolationLevel.ReadCommitted, config.IsolationLevel);
        Assert.Equal(PartitionAssignmentStrategy.Range, config.PartitionAssignmentStrategy);
        Assert.Equal(300_000, config.MaxPollIntervalMs);
        Assert.Equal(10_000, config.SessionTimeoutMs);
        Assert.Equal(10_000, config.SocketTimeoutMs);
        Assert.Equal(500, config.FetchWaitMaxMs);
        Assert.Equal(65_536, config.MessageMaxBytes);
        Assert.Equal(65_536, config.FetchMaxBytes);
        Assert.Equal(65_536, config.MaxPartitionFetchBytes);
        Assert.Equal(1_049_088, config.ReceiveMessageMaxBytes);
        Assert.Equal(1_024, config.QueuedMaxMessagesKbytes);
        Assert.Equal(1, config.QueuedMinMessages);
        Assert.Equal(SecurityProtocol.Plaintext, config.SecurityProtocol);
        Assert.True(config.EnableSslCertificateVerification);
        Assert.Equal(SslEndpointIdentificationAlgorithm.Https, config.SslEndpointIdentificationAlgorithm);
        Assert.Null(config.SaslUsername);
        Assert.Null(config.SaslPassword);
    }

    [Fact]
    public void UnknownCatalogProjectIsRejectedEvenWhenKeyConfigurationIsIndependent()
    {
        var options = KafkaTestSupport.Options();
        var catalogOptions = KafkaTestSupport.Options();
        catalogOptions.ProjectSchemas = [new() { ProjectId = "other" }];
        var catalog = new ProjectCatalog(catalogOptions, ProjectContext.Compile("base", catalogOptions));
        var keys = new Nealytics.Engine.Infrastructure.Security.ApiKeyValidator(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Nealytics.Engine.Infrastructure.Security.ApiKeyValidator>.Instance);
        Assert.Contains("schema context", Assert.Throws<InvalidOperationException>(() => KafkaConnectorSettings.Create(options.Kafka!, keys, catalog)).Message);
    }

    [Fact]
    public void LiteralNamesAcceptAsciiDigitsAndTopicPunctuation()
    {
        var options = KafkaTestSupport.Options();
        options.Kafka!.Topic = "a.0_b-9";
        options.Kafka.GroupId = "0.x_y-z";
        var settings = KafkaTestSupport.Settings(options);
        Assert.Equal("a.0_b-9", settings.Topic);
        Assert.Equal("0.x_y-z", settings.Consumer.GroupId);
    }

    [Theory]
    [InlineData("earliest", AutoOffsetReset.Earliest)]
    [InlineData("latest", AutoOffsetReset.Latest)]
    [InlineData("error", AutoOffsetReset.Error)]
    public void InitialOffsetIsExplicit(string text, AutoOffsetReset expected)
    {
        var options = KafkaTestSupport.Options();
        options.Kafka!.InitialOffset = text;
        Assert.Equal(expected, KafkaTestSupport.Settings(options).Consumer.AutoOffsetReset);
    }

    [Theory]
    [InlineData("plain", SaslMechanism.Plain)]
    [InlineData("scram-sha-256", SaslMechanism.ScramSha256)]
    [InlineData("scram-sha-512", SaslMechanism.ScramSha512)]
    public void SaslRequiresTlsAndPreservesCredentialsAndCa(string mechanism, SaslMechanism expected)
    {
        var options = KafkaTestSupport.Options();
        options.Kafka!.SecurityProtocol = "sasl_ssl";
        options.Kafka.SaslMechanism = mechanism;
        options.Kafka.SaslUsername = "source-user";
        options.Kafka.SaslPassword = "source-password";
        options.Kafka.SslCaLocation = "/certs/ca.pem";
        var config = KafkaTestSupport.Settings(options).Consumer;
        Assert.Equal(SecurityProtocol.SaslSsl, config.SecurityProtocol);
        Assert.Equal(expected, config.SaslMechanism);
        Assert.Equal("source-user", config.SaslUsername);
        Assert.Equal("source-password", config.SaslPassword);
        Assert.Equal("/certs/ca.pem", config.SslCaLocation);
    }

    [Fact]
    public void SslIsAcceptedWithoutSasl()
    {
        var options = KafkaTestSupport.Options();
        options.Kafka!.SecurityProtocol = "ssl";
        Assert.Equal(SecurityProtocol.Ssl, KafkaTestSupport.Settings(options).Consumer.SecurityProtocol);
    }

    public static IEnumerable<object[]> InvalidConfigurations()
    {
        yield return [new Action<TelemetryEngineOptions>(o => o.Kafka!.BootstrapServers = " ")];
        yield return [new Action<TelemetryEngineOptions>(o => o.Kafka!.BootstrapServers = new string('x', 4_097))];
        foreach (var name in new string?[] { null, "", ".", "..", "^events", "topic/name", "é", new('x', 250) })
            yield return [new Action<TelemetryEngineOptions>(o => o.Kafka!.Topic = name!)];
        foreach (var name in new string?[] { null, "", "group/name", new('x', 257) })
            yield return [new Action<TelemetryEngineOptions>(o => o.Kafka!.GroupId = name!)];
        foreach (int size in new[] { 1_023, 1_048_577 })
            yield return [new Action<TelemetryEngineOptions>(o => o.Kafka!.MaxMessageBytes = size)];
        foreach (int size in new[] { 0, 65_537, 63 })
            yield return [new Action<TelemetryEngineOptions>(o => o.Kafka!.QueueBufferKiB = size)];
        foreach (int size in new[] { 59_999, 900_001 })
            yield return [new Action<TelemetryEngineOptions>(o => o.Kafka!.MaxPollIntervalMs = size)];
        yield return [new Action<TelemetryEngineOptions>(o => o.Kafka!.ProjectKey = "unknown-secret")];
        yield return [new Action<TelemetryEngineOptions>(o => o.IngestionKeys[0].Scope = "public")];
        yield return [new Action<TelemetryEngineOptions>(o => o.IngestionKeys[0].ProjectId = "")];
        yield return [new Action<TelemetryEngineOptions>(o => o.Kafka!.InitialOffset = "invalid")];
        yield return [new Action<TelemetryEngineOptions>(o => o.Kafka!.SecurityProtocol = "sasl_plaintext")];
        yield return [new Action<TelemetryEngineOptions>(o => o.Kafka!.SecurityProtocol = "SSL")];
        yield return [new Action<TelemetryEngineOptions>(o => { o.Kafka!.SecurityProtocol = "sasl_ssl"; o.Kafka.SaslPassword = "secret"; })];
        yield return [new Action<TelemetryEngineOptions>(o => { o.Kafka!.SecurityProtocol = "sasl_ssl"; o.Kafka.SaslUsername = "user"; })];
        yield return [new Action<TelemetryEngineOptions>(o => { o.Kafka!.SecurityProtocol = "sasl_ssl"; o.Kafka.SaslUsername = "user"; o.Kafka.SaslPassword = "secret"; o.Kafka.SaslMechanism = "oauthbearer"; })];
        yield return [new Action<TelemetryEngineOptions>(o => o.ProjectSchemas = [new() { ProjectId = "other" }])];
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void InvalidConfigurationFailsWithoutEchoingCredentials(Action<TelemetryEngineOptions> change)
    {
        var options = KafkaTestSupport.Options();
        change(options);
        var error = Assert.Throws<InvalidOperationException>(() => KafkaTestSupport.Settings(options));
        Assert.StartsWith("Kafka ", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Theory]
    [InlineData(1_024, 1, 60_000)]
    [InlineData(1_024, 1, 900_000)]
    [InlineData(1_048_576, 65_536, 300_000)]
    public void ExactResourceBoundsAreAccepted(int message, int queue, int poll)
    {
        var options = KafkaTestSupport.Options();
        options.Kafka!.MaxMessageBytes = message;
        options.Kafka.QueueBufferKiB = queue;
        options.Kafka.MaxPollIntervalMs = poll;
        options.Kafka.Topic = new string('a', 249);
        options.Kafka.GroupId = new string('b', 256);
        options.Kafka.BootstrapServers = new string('c', 4_096);
        var config = KafkaTestSupport.Settings(options).Consumer;
        Assert.Equal(message, config.MessageMaxBytes);
        Assert.Equal(message, config.FetchMaxBytes);
        Assert.Equal(message, config.MaxPartitionFetchBytes);
        Assert.Equal(queue * 1_024 + 512, config.ReceiveMessageMaxBytes);
        Assert.Equal(queue, config.QueuedMaxMessagesKbytes);
        Assert.Equal(poll, config.MaxPollIntervalMs);
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Nealytics.Engine.Tests.Integration;

internal sealed class KafkaSecurityBroker : IAsyncDisposable
{
    private const string Image = "apache/kafka:4.1.1@sha256:0bc1bb2478f45b6cea78864df86acdc11e8df2c5172477819a4d12942cbe5d40";
    private const UnixFileModes PrivateMode = UnixFileModes.UserRead | UnixFileModes.UserWrite;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-kafka-security-" + Guid.NewGuid().ToString("N"));
    private readonly IContainer _broker;

    public KafkaSecurityBroker()
    {
        Directory.CreateDirectory(_directory);
        using var key = RSA.Create(2048);
        using var ca = Authority(key);
        File.WriteAllText(CaLocation, ca.ExportCertificatePem());
        using var otherKey = RSA.Create(2048);
        using var otherCa = Authority(otherKey);
        File.WriteAllText(WrongCaLocation, otherCa.ExportCertificatePem());
        _broker = new ContainerBuilder(Image)
            .WithLabel("nealytics.scope", "kafka-security-" + Guid.NewGuid().ToString("N"))
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig ??= new();
                parameters.HostConfig.Memory = 1_073_741_824;
                parameters.HostConfig.NanoCPUs = 2_000_000_000;
                if (parameters.HostConfig.PortBindings is not null)
                {
                    foreach (var bindings in parameters.HostConfig.PortBindings.Values)
                    {
                        foreach (var binding in bindings)
                        {
                            binding.HostIP = "127.0.0.1";
                        }
                    }
                }
            })
            .WithPortBinding(9092, true)
            .WithPortBinding(9094, true)
            .WithPortBinding(9095, true)
            .WithPortBinding(9096, true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(Leaf(ca, false)), "/tmp/fixture-certs/server.pem", 1000, 1000, PrivateMode)
            .WithResourceMapping(Encoding.UTF8.GetBytes(Leaf(ca, true)), "/tmp/fixture-certs/mismatch.pem", 1000, 1000, PrivateMode)
            .WithEnvironment("KAFKA_HEAP_OPTS", "-Xms256m -Xmx512m")
            .WithEntrypoint("/bin/sh", "-c")
            .WithCommand("sleep infinity")
            .Build();
    }

    public string Username => "fixture";
    public string Password { get; } = "fixture-password-" + Guid.NewGuid().ToString("N");
    public string CaLocation => Path.Combine(_directory, "ca.pem");
    public string WrongCaLocation => Path.Combine(_directory, "wrong-ca.pem");
    public string AdminServers => Address(9092);
    public string Servers(string mode, bool mismatch = false) => Address(mismatch ? 9096 : mode == "ssl" ? 9094 : 9095);
    private string Address(int port) => _broker.Hostname + ":" + _broker.GetMappedPublicPort(port);

    public async Task StartAsync()
    {
        await _broker.StartAsync();
        string properties = $"""
            process.roles=broker,controller
            node.id=1
            controller.quorum.voters=1@localhost:9093
            controller.listener.names=CONTROLLER
            inter.broker.listener.name=ADMIN
            listeners=ADMIN://0.0.0.0:9092,CONTROLLER://0.0.0.0:9093,TLS://0.0.0.0:9094,AUTH://0.0.0.0:9095,MISMATCH://0.0.0.0:9096
            advertised.listeners=ADMIN://{AdminServers},TLS://{Servers("ssl")},AUTH://{Servers("plain")},MISMATCH://{Servers("ssl", true)}
            listener.security.protocol.map=ADMIN:PLAINTEXT,CONTROLLER:PLAINTEXT,TLS:SSL,AUTH:SASL_SSL,MISMATCH:SSL
            ssl.keystore.type=PEM
            ssl.keystore.location=/tmp/fixture-certs/server.pem
            ssl.client.auth=none
            listener.name.mismatch.ssl.keystore.location=/tmp/fixture-certs/mismatch.pem
            sasl.enabled.mechanisms=PLAIN,SCRAM-SHA-256,SCRAM-SHA-512
            listener.name.auth.plain.sasl.jaas.config=org.apache.kafka.common.security.plain.PlainLoginModule required user_fixture="{Password}";
            listener.name.auth.scram-sha-256.sasl.jaas.config=org.apache.kafka.common.security.scram.ScramLoginModule required;
            listener.name.auth.scram-sha-512.sasl.jaas.config=org.apache.kafka.common.security.scram.ScramLoginModule required;
            log.dirs=/tmp/security-kafka-logs
            offsets.topic.replication.factor=1
            transaction.state.log.replication.factor=1
            transaction.state.log.min.isr=1
            group.initial.rebalance.delay.ms=0
            auto.create.topics.enable=false
            """;
        await _broker.CopyAsync(Encoding.UTF8.GetBytes(properties), "/tmp/security-server.properties", 1000, 1000, PrivateMode);
        var formatted = await _broker.ExecAsync(["/opt/kafka/bin/kafka-storage.sh", "format", "--cluster-id=" +
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            "--config", "/tmp/security-server.properties"]);
        Assert.True(formatted.ExitCode == 0, (formatted.Stdout + formatted.Stderr).Replace(Password, "[redacted]"));
        var started = await _broker.ExecAsync(["/bin/sh", "-c",
            "/opt/kafka/bin/kafka-server-start.sh /tmp/security-server.properties >>/tmp/security-broker.log 2>&1 & echo $! >/tmp/security-server.pid"]);
        Assert.Equal(0, started.ExitCode);
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = AdminServers }).SetLogHandler((_, _) => { }).Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            try
            {
                if (admin.GetMetadata(TimeSpan.FromSeconds(1)).Brokers.Count > 0) break;
            }
            catch (KafkaException) { }
            await Task.Delay(100, deadline.Token);
        }
        try
        {
            foreach (var mechanism in new[] { ScramMechanism.ScramSha256, ScramMechanism.ScramSha512 })
            {
                await admin.AlterUserScramCredentialsAsync([new UserScramCredentialUpsertion
                {
                    User = Username, Password = Encoding.UTF8.GetBytes(Password),
                    ScramCredentialInfo = new() { Mechanism = mechanism, Iterations = 4096 }
                }], new() { RequestTimeout = TimeSpan.FromSeconds(15) });
            }
        }
        catch (AlterUserScramCredentialsException error)
        {
            throw new InvalidOperationException(string.Join("; ", error.Results.Select(result => result.Error.ToString())).Replace(Password, "[redacted]"));
        }
    }

    public Nealytics.Engine.Infrastructure.Configuration.KafkaConnectorOptions Options(string mode, string group, string projectKey, string? fault = null) => new()
    {
        Enabled = true, BootstrapServers = Servers(mode, fault == "hostname"), GroupId = group,
        SecurityProtocol = mode == "ssl" ? "ssl" : "sasl_ssl", SslCaLocation = fault == "trust" ? WrongCaLocation : CaLocation,
        SaslMechanism = mode, SaslUsername = Username, SaslPassword = fault == "password" ? "wrong-" + Password : Password,
        ProjectKey = projectKey, InitialOffset = "earliest"
    };

    public async Task StopServerAsync()
    {
        var stopped = await _broker.ExecAsync(["/bin/sh", "-c", "kill -TERM \"$(cat /tmp/security-server.pid)\""]);
        Assert.Equal(0, stopped.ExitCode);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var state = await _broker.ExecAsync(["/bin/sh", "-c",
                "file=/proc/$(cat /tmp/security-server.pid)/stat; if [ -r \"$file\" ]; then awk '{print $3}' \"$file\"; fi"]);
            if (state.Stdout.Trim() is "" or "Z") return;
            await Task.Delay(100, deadline.Token);
        }
    }

    public async Task RestartServerAsync()
    {
        var started = await _broker.ExecAsync(["/bin/sh", "-c",
            "/opt/kafka/bin/kafka-server-start.sh /tmp/security-server.properties >>/tmp/security-broker.log 2>&1 & echo $! >/tmp/security-server.pid"]);
        Assert.Equal(0, started.ExitCode);
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = AdminServers }).SetLogHandler((_, _) => { }).Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            try { if (admin.GetMetadata(TimeSpan.FromSeconds(1)).Brokers.Count > 0) return; }
            catch (KafkaException) { }
            await Task.Delay(100, deadline.Token);
        }
    }

    public async Task<string> BrokerLogAsync() => (await _broker.ExecAsync(["cat", "/tmp/security-broker.log"])).Stdout;

    private static X509Certificate2 Authority(RSA key)
    {
        var request = new CertificateRequest("CN=Nealytics ephemeral fixture CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
    }

    private static string Leaf(X509Certificate2 ca, bool mismatch)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + (mismatch ? "unexpected.invalid" : "broker"), key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(mismatch ? "unexpected.invalid" : "broker");
        if (!mismatch) { names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); names.AddIpAddress(IPAddress.IPv6Loopback); }
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        return key.ExportPkcs8PrivateKeyPem() + "\n" + certificate.ExportCertificatePem() + "\n" + ca.ExportCertificatePem();
    }

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

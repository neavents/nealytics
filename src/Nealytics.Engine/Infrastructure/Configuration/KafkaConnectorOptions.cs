namespace Nealytics.Engine.Infrastructure.Configuration;

public sealed class KafkaConnectorOptions
{
    public bool Enabled { get; set; }
    public string BootstrapServers { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = string.Empty;
    public string InitialOffset { get; set; } = "error";
    public string SecurityProtocol { get; set; } = "ssl";
    public string SaslMechanism { get; set; } = "plain";
    public string SaslUsername { get; set; } = string.Empty;
    public string SaslPassword { get; set; } = string.Empty;
    public string? SslCaLocation { get; set; }
    public int MaxMessageBytes { get; set; } = 65_536;
    public int QueueBufferKiB { get; set; } = 1_024;
    public int MaxPollIntervalMs { get; set; } = 300_000;
}

namespace Nealytics.Engine.Infrastructure.Configuration;

public sealed class GcsSourceOptions
{
    public string Name { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Endpoint { get; set; } = "https://storage.googleapis.com";
    public string Bucket { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string IdentityKey { get; set; } = "";
    public bool AllowInsecureHttp { get; set; }
    public long MaxObjectBytes { get; set; } = 1_099_511_627_776;
    public int RequestTimeoutSeconds { get; set; } = 30;
}

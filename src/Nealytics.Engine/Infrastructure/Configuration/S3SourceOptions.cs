namespace Nealytics.Engine.Infrastructure.Configuration;

public sealed class S3SourceOptions
{
    public string Name { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string Region { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string SessionToken { get; set; } = "";
    public bool PathStyle { get; set; } = true;
    public bool AllowInsecureHttp { get; set; }
    public long MaxObjectBytes { get; set; } = 1_099_511_627_776;
    public int RequestTimeoutSeconds { get; set; } = 30;
}

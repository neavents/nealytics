namespace Nealytics.Engine.Infrastructure.Configuration;

using System.Collections.Generic;

public sealed class HttpSourceOptions
{
    public string Name { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Url { get; set; } = "";
    public string IdentityKey { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = [];
    public bool AllowInsecureHttp { get; set; }
    public long MaxObjectBytes { get; set; } = 1_099_511_627_776;
    public int RequestTimeoutSeconds { get; set; } = 30;
}

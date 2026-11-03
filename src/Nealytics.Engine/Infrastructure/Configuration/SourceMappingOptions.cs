namespace Nealytics.Engine.Infrastructure.Configuration;

using System.Collections.Generic;

public sealed class SourceMappingOptions
{
    public Dictionary<string, SourceFieldOptions> Fields { get; set; } = [];
}

public sealed class SourceFieldOptions
{
    public string[] Path { get; set; } = [];
    public string? Default { get; set; }
}

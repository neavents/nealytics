namespace Nealytics.Engine.Infrastructure.Configuration;

using System.Collections.Generic;

public sealed class DimensionMappingOptions
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

public sealed class ComputedDimensionOptions
{
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string Source { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public List<DimensionMappingOptions> Mapping { get; set; } = [];
    public string? Default { get; set; }
    public int Start { get; set; } = 1;
    public int Length { get; set; } = 64;
    public double? Width { get; set; }
}

public sealed record DimensionMapping(string From, string To);

public sealed record ComputedDimension(
    string Name, int Version, string Source, string Operation, IReadOnlyList<DimensionMapping> Mapping,
    string? Default, int Start, int Length, double? Width)
{
    internal string Expression { get; init; } = string.Empty;
    internal bool Numeric { get; init; }
    internal int Depth { get; init; }
}

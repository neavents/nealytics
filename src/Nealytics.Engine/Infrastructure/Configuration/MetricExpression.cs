namespace Nealytics.Engine.Infrastructure.Configuration;

using System.Collections.Generic;

public sealed class MetricExpressionOptions
{
    public string Operator { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public List<MetricExpressionOptions> Arguments { get; set; } = [];
}

public sealed record MetricExpression(
    string Operator, string? Value, string? Metric, IReadOnlyList<MetricExpression> Arguments);

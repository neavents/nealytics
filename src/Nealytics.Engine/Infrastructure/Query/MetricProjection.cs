namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Collections.Generic;
using System.Linq;
using Nealytics.Engine.Infrastructure.Configuration;

internal sealed class MetricProjection
{
    internal MetricProjection(IEnumerable<NamedMetric> metrics)
    {
        Dictionary<string, NamedMetric> components = new(StringComparer.Ordinal);
        foreach (NamedMetric metric in metrics)
        {
            if (metric.CompiledExpression is FormulaNode expression)
            {
                expression.Collect(components);
            }
            else
            {
                components.TryAdd(metric.Name, metric);
            }
        }
        Components = Array.AsReadOnly(components.Values.ToArray());
    }

    internal IReadOnlyList<NamedMetric> Components { get; }

    internal static string? Evaluate(NamedMetric metric, IReadOnlyDictionary<string, string?> values) =>
        metric.CompiledExpression is FormulaNode expression
            ? expression.Evaluate(values)?.Format() : values[metric.Name];

    internal static string? UnitViolation(
        NamedMetric metric, MeasureRegistry measures, IReadOnlyList<QueryFilter> filters,
        IReadOnlyList<string>? groupBy = null)
    {
        IReadOnlyList<NamedMetric> components = metric.CompiledExpression is null ? [metric] : metric.Dependencies;
        foreach (NamedMetric component in components)
        {
            if (component.Measure is string measure)
            {
                string? unitGroup = component.UnitDimension is string dimension && groupBy is not null && groupBy.Contains(dimension)
                    ? dimension : null;
                string? violation = UnitSafety.Violation(measures.Find(measure)!, component.Aggregation, unitGroup, filters);
                if (violation is not null)
                {
                    return violation;
                }
            }
        }
        return null;
    }

    internal static string? NormalizeValue(string? value) => value is "nan" or "inf" or "-inf" ? null : value;
}

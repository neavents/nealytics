namespace Nealytics.Engine.Infrastructure.Query;

using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Configuration;

internal sealed record FormulaNode(
    string Operator, FormulaNumber? Constant, NamedMetric? Metric, IReadOnlyList<FormulaNode> Arguments,
    FormulaUnit? UnitKey, string? Unit)
{
    internal FormulaNumber? Evaluate(IReadOnlyDictionary<string, string?> values)
    {
        if (Operator == "constant")
        {
            return Constant;
        }
        if (Operator == "metric")
        {
            return Metric!.CompiledExpression is FormulaNode expression
                ? expression.Evaluate(values) : FormulaNumber.Parse(values[Metric.Name]);
        }
        FormulaNumber? left = Arguments[0].Evaluate(values);
        if (left is null)
        {
            return null;
        }
        if (Operator == "negate")
        {
            return left.Value.Negate();
        }
        FormulaNumber? right = Arguments[1].Evaluate(values);
        return right is null ? null : FormulaNumber.Apply(Operator, left.Value, right.Value);
    }

    internal void Collect(Dictionary<string, NamedMetric> metrics)
    {
        if (Metric is NamedMetric metric)
        {
            if (metric.CompiledExpression is FormulaNode expression)
            {
                expression.Collect(metrics);
            }
            else
            {
                metrics.TryAdd(metric.Name, metric);
            }
        }
        foreach (FormulaNode argument in Arguments)
        {
            argument.Collect(metrics);
        }
    }
}

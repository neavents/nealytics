namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Nealytics.Engine.Infrastructure.Query;

internal sealed class MetricFormulaCompiler
{
    private static readonly Regex ConstantPattern = new("^-?[0-9]+(?:\\.[0-9]+)?$", RegexOptions.Compiled);
    private readonly IReadOnlyList<NamedMetricOptions> _options;
    private readonly NamedMetric[] _definitions;
    private readonly Dictionary<string, int> _indices;
    private readonly byte[] _states;
    private int _visiting;

    internal MetricFormulaCompiler(IReadOnlyList<NamedMetricOptions> options, IReadOnlyList<NamedMetric> definitions)
    {
        _options = options;
        _definitions = definitions.ToArray();
        _indices = definitions.Select((metric, index) => (metric.Name, index))
            .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.Ordinal);
        _states = new byte[definitions.Count];
    }

    internal IReadOnlyList<NamedMetric> Compile()
    {
        for (int index = 0; index < _definitions.Length; index++)
        {
            Resolve(index);
        }
        return _definitions;
    }

    private NamedMetric Resolve(int index)
    {
        NamedMetric metric = _definitions[index];
        if (metric.Aggregation != "formula" || _states[index] == 2)
        {
            return metric;
        }
        if (_states[index] == 1)
        {
            throw Invalid(index, "formula dependencies contain a cycle");
        }
        if (++_visiting > 8)
        {
            throw Invalid(index, "formula dependency depth exceeds eight");
        }
        _states[index] = 1;
        int count = 0;
        (MetricExpression expression, FormulaNode node) = CompileNode(_options[index].Expression!, index, 1, ref count);
        string? unit = metric.Unit ?? node.Unit;
        if (node.UnitKey is not null && metric.Unit is not null && metric.Unit != node.Unit)
        {
            throw Invalid(index, "formula unit must match its inferred dimensional unit");
        }
        Dictionary<string, NamedMetric> dependencies = new(StringComparer.Ordinal);
        node.Collect(dependencies);
        if (metric.Accuracy == "exact" && dependencies.Values.Any(dependency => dependency.Accuracy == "approximate"))
        {
            throw Invalid(index, "an exact formula cannot contain an approximate dependency");
        }
        string[] unitDimensions = dependencies.Values.Select(dependency => dependency.UnitDimension)
            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        NamedMetric compiled = metric with
        {
            Unit = unit,
            UnitDimension = node.UnitKey is not null && unitDimensions.Length == 1 ? unitDimensions[0] : null,
            Expression = expression,
            CompiledExpression = node,
            Dependencies = Array.AsReadOnly(dependencies.Values.ToArray())
        };
        _definitions[index] = compiled;
        _states[index] = 2;
        _visiting--;
        return compiled;
    }

    private (MetricExpression Expression, FormulaNode Node) CompileNode(
        MetricExpressionOptions declaration, int index, int depth, ref int count)
    {
        if (declaration is null || depth > 8 || ++count > 64)
        {
            throw Invalid(index, "formula requires nonnull nodes, depth at most eight and at most 64 expanded nodes");
        }
        string? operation = declaration.Operator?.Trim().ToLowerInvariant();
        int arguments = operation switch
        {
            "constant" or "metric" => 0,
            "negate" => 1,
            "add" or "subtract" or "multiply" or "divide" => 2,
            _ => throw Invalid(index, "formula operator is not supported")
        };
        if (declaration.Arguments is null || declaration.Arguments.Count != arguments)
        {
            throw Invalid(index, "formula operator has an invalid argument count");
        }
        string? value = Normalize(declaration.Value);
        string? reference = Normalize(declaration.Metric);
        if (operation == "constant")
        {
            if (reference is not null || value is null || !ConstantPattern.IsMatch(value)
                || value.Count(character => character is >= '0' and <= '9') > 38)
            {
                throw Invalid(index, "formula constants require a decimal value with at most 38 digits");
            }
            return (new MetricExpression(operation, value, null, Array.Empty<MetricExpression>()),
                new FormulaNode(operation, FormulaNumber.Parse(value), null, Array.Empty<FormulaNode>(), null, null));
        }
        if (value is not null || (operation != "metric" && reference is not null))
        {
            throw Invalid(index, "formula node contains fields unsupported by its operator");
        }
        if (operation == "metric")
        {
            int dependency = default;
            if (reference is null || !_indices.TryGetValue(reference, out dependency))
            {
                throw Invalid(index, "formula metric reference is not declared");
            }
            NamedMetric metric = Resolve(dependency);
            if (metric.Expression is MetricExpression nested)
            {
                CountExpanded(nested, index, depth + 1, ref count);
            }
            FormulaUnit? key = PrimitiveUnit(metric);
            return (new MetricExpression(operation, null, metric.Name, Array.Empty<MetricExpression>()),
                new FormulaNode(operation, null, metric, Array.Empty<FormulaNode>(), key, metric.Unit));
        }
        MetricExpression[] expressions = new MetricExpression[arguments];
        FormulaNode[] nodes = new FormulaNode[arguments];
        for (int argument = 0; argument < arguments; argument++)
        {
            (expressions[argument], nodes[argument]) = CompileNode(declaration.Arguments[argument], index, depth + 1, ref count);
        }
        FormulaUnit? unitKey = InferUnit(operation, nodes, index);
        string? unit = operation == "negate" ? nodes[0].Unit
            : unitKey is FormulaUnit inferred && inferred.Kind == FormulaUnitKind.Annotation ? inferred.Name : null;
        return (new MetricExpression(operation!, null, null, Array.AsReadOnly(expressions)),
            new FormulaNode(operation!, null, null, Array.AsReadOnly(nodes), unitKey, unit));
    }

    private void CountExpanded(MetricExpression expression, int index, int depth, ref int count)
    {
        if (depth > 8 || ++count > 64)
        {
            throw Invalid(index, "formula expanded depth or node count exceeds its bound");
        }
        if (expression.Metric is string name && _definitions[_indices[name]].Expression is MetricExpression nested)
        {
            CountExpanded(nested, index, depth + 1, ref count);
        }
        foreach (MetricExpression argument in expression.Arguments)
        {
            CountExpanded(argument, index, depth + 1, ref count);
        }
    }

    private static FormulaUnit? PrimitiveUnit(NamedMetric metric) =>
        metric.Aggregation == "formula" ? metric.CompiledExpression!.UnitKey
        : metric.Unit is string unit ? new FormulaUnit(FormulaUnitKind.Annotation, unit, metric.UnitDimension)
        : metric.UnitDimension is string dimension ? new FormulaUnit(FormulaUnitKind.Dimension, dimension, null)
        : metric.Measure is string measure && metric.Aggregation != "count" ? new FormulaUnit(FormulaUnitKind.Measure, measure, null) : null;

    private static FormulaUnit? InferUnit(string operation, IReadOnlyList<FormulaNode> nodes, int index)
    {
        FormulaUnit? left = nodes[0].UnitKey;
        if (operation == "negate")
        {
            return left;
        }
        FormulaUnit? right = nodes[1].UnitKey;
        return operation switch
        {
            "add" or "subtract" when left == right => left,
            "multiply" when left is null || right is null => left is null ? right : left,
            "divide" when left == right => null,
            "divide" when right is null => left,
            _ => throw Invalid(index, "formula combines incompatible dimensional units")
        };
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static InvalidOperationException Invalid(int index, string reason) =>
        new($"TelemetryEngine:NamedMetrics[{index}]: {reason}.");
}

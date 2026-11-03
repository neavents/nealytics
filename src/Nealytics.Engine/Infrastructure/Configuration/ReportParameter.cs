namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Nealytics.Engine.Infrastructure.Query;

public sealed class ReportParameterOptions
{
    public string Name { get; set; } = string.Empty;
    public string Column { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public string Comparison { get; set; } = "eq";
    public bool Required { get; set; }
    public string? Default { get; set; }
}

public sealed record ReportParameter(
    string Name, string Column, string Type, string Comparison, bool Required, string? Default)
{
    internal FilterComparison Predicate { get; init; }
}

internal static class ReportParameterCompiler
{
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled);

    internal static IReadOnlyList<ReportParameter> Compile(
        IReadOnlyList<ReportParameterOptions>? declarations, QueryColumns columns, MeasureRegistry measures)
    {
        if (declarations is null || declarations.Count > 16)
        {
            throw new InvalidOperationException("Report parameters require a nonnull list with at most 16 declarations.");
        }
        ReportParameter[] parameters = new ReportParameter[declarations.Count];
        HashSet<string> names = new(StringComparer.Ordinal);
        for (int index = 0; index < declarations.Count; index++)
        {
            ReportParameterOptions declaration = declarations[index]
                ?? throw new InvalidOperationException("Report parameter declaration is null.");
            string? name = declaration.Name?.Trim();
            string? column = declaration.Column?.Trim();
            string? type = declaration.Type?.Trim().ToLowerInvariant();
            string? comparison = declaration.Comparison?.Trim().ToLowerInvariant();
            if (name is null || !NamePattern.IsMatch(name) || !names.Add(name))
            {
                throw new InvalidOperationException("Report parameter names must be unique and match [a-z][a-z0-9_]{0,62}.");
            }
            if (column is null)
            {
                throw new InvalidOperationException("Report parameter type must match an active string dimension or numeric measure.");
            }
            bool numeric = type == "number";
            if (numeric ? measures.Find(column) is null
                : type != "string" || !columns.TryResolve(column, out _))
            {
                throw new InvalidOperationException("Report parameter type must match an active string dimension or numeric measure.");
            }
            FilterComparison predicate = comparison switch
            {
                "eq" => FilterComparison.Equal,
                "ne" when numeric => FilterComparison.NotEqual,
                "lt" when numeric => FilterComparison.Less,
                "lte" when numeric => FilterComparison.LessOrEqual,
                "gt" when numeric => FilterComparison.Greater,
                "gte" when numeric => FilterComparison.GreaterOrEqual,
                _ => throw new InvalidOperationException("Report parameter comparison is unsupported for its type.")
            };
            if (declaration.Default is string value && !ReportParameterBinding.IsValidValue(type!, value))
            {
                throw new InvalidOperationException("Report parameter default must be a nonblank bounded string or finite number.");
            }
            parameters[index] = new ReportParameter(name, column!, type!, comparison!, declaration.Required, declaration.Default)
            {
                Predicate = predicate
            };
        }
        return Array.AsReadOnly(parameters);
    }
}

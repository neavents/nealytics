namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nealytics.Engine.Infrastructure.Configuration;

internal readonly record struct ReportParameterResult(IReadOnlyList<QueryFilter> Filters, string? Error);

internal static class ReportParameterBinding
{
    internal static ReportParameterResult Bind(
        IReadOnlyList<ReportParameter> declarations, IReadOnlyDictionary<string, string[]>? values)
    {
        if (values is not null)
        {
            foreach (string name in values.Keys)
            {
                if (!declarations.Any(parameter => parameter.Name == name))
                {
                    return new(Array.Empty<QueryFilter>(), $"Unknown report parameter '{name}'.");
                }
            }
        }
        List<QueryFilter> filters = new(declarations.Count);
        foreach (ReportParameter parameter in declarations)
        {
            string? value = parameter.Default;
            string[]? supplied = null;
            if (values is not null && values.TryGetValue(parameter.Name, out supplied))
            {
                if (supplied is null || supplied.Length != 1 || supplied[0] is null)
                {
                    return new(Array.Empty<QueryFilter>(), $"Report parameter '{parameter.Name}' requires exactly one value.");
                }
                value = supplied[0];
            }
            if (value is null)
            {
                if (parameter.Required)
                {
                    return new(Array.Empty<QueryFilter>(), $"Report parameter '{parameter.Name}' is required.");
                }
                continue;
            }
            if (!IsValidValue(parameter.Type, value))
            {
                return new(Array.Empty<QueryFilter>(), $"Report parameter '{parameter.Name}' requires a nonblank value of at most 256 characters matching its type.");
            }
            filters.Add(new QueryFilter
            {
                Column = parameter.ResolvedColumn,
                Value = value,
                Comparison = parameter.Predicate,
                IsMeasure = parameter.Type == "number"
            });
        }
        return new(Array.AsReadOnly(filters.ToArray()), null);
    }

    internal static bool IsValidValue(string type, string value)
    {
        double numeric = default;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 256
            && (type == "string" || type == "number" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out numeric) && double.IsFinite(numeric));
    }
}

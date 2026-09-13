namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Collections.Generic;
using System.Globalization;
using Nealytics.Engine.Infrastructure.Configuration;

public static class FilterParser
{
    public enum Outcome
    {
        Ok,
        Malformed,
        UnknownColumn,
        ValueTooLong,
        NotANumber,
    }

    public readonly struct Result
    {
        public Outcome Outcome { get; init; }
        public IReadOnlyList<QueryFilter> Filters { get; init; }
        public string Offender { get; init; }
        public string Column { get; init; }
    }

    private static readonly (string Token, FilterComparison Comparison)[] Comparisons =
    [
        ("<=", FilterComparison.LessOrEqual),
        (">=", FilterComparison.GreaterOrEqual),
        ("!=", FilterComparison.NotEqual),
        ("<", FilterComparison.Less),
        (">", FilterComparison.Greater),
        ("=", FilterComparison.Equal),
    ];

    public static Result Parse(IReadOnlyList<string> raw, QueryColumns columns, int maxValueLength) =>
        Parse(raw, columns, null, maxValueLength);

    public static Result Parse(
        IReadOnlyList<string> raw, QueryColumns columns, MeasureRegistry? measures, int maxValueLength)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(columns);

        List<QueryFilter> filters = new(raw.Count);

        foreach (string entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            int separator = entry.IndexOf(':', StringComparison.Ordinal);

            if (separator > 0 && IndexOfComparison(entry.AsSpan(0, separator)) < 0)
            {
                string name = entry[..separator];
                string value = entry[(separator + 1)..];

                if (!columns.TryResolve(name, out string column))
                {
                    return Fail(Outcome.UnknownColumn, entry, name);
                }

                if (value.Length > maxValueLength)
                {
                    return Fail(Outcome.ValueTooLong, entry, name);
                }

                filters.Add(QueryFilter.Equals(column, value));
                continue;
            }

            int operatorAt = IndexOfComparison(entry);

            if (operatorAt <= 0 || measures is null)
            {
                return Fail(Outcome.Malformed, entry, string.Empty);
            }

            (string Token, FilterComparison Comparison)? matched = ComparisonAt(entry, operatorAt);

            if (matched is null)
            {
                return Fail(Outcome.Malformed, entry, string.Empty);
            }

            (string token, FilterComparison comparison) = matched.Value;
            string measureName = entry[..operatorAt];
            string number = entry[(operatorAt + token.Length)..];
            Measure? measure = measures.Find(measureName);

            if (measure is null)
            {
                return Fail(Outcome.UnknownColumn, entry, measureName);
            }

            if (number.Length == 0 || number.Length > maxValueLength
                || !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                || !double.IsFinite(parsed))
            {
                return Fail(Outcome.NotANumber, entry, measureName);
            }

            filters.Add(new QueryFilter
            {
                Column = measure.Name,
                Value = parsed.ToString("R", CultureInfo.InvariantCulture),
                Comparison = comparison,
                IsMeasure = true,
            });
        }

        return new Result { Outcome = Outcome.Ok, Filters = filters, Offender = string.Empty, Column = string.Empty };
    }

    private static Result Fail(Outcome outcome, string offender, string column) =>
        new() { Outcome = outcome, Offender = offender, Column = column, Filters = [] };

    private static int IndexOfComparison(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '<' or '>' or '=' or '!')
            {
                return i;
            }
        }

        return -1;
    }

    private static (string Token, FilterComparison Comparison)? ComparisonAt(string entry, int index)
    {
        foreach ((string token, FilterComparison comparison) in Comparisons)
        {
            if (index + token.Length <= entry.Length
                && string.CompareOrdinal(entry, index, token, 0, token.Length) == 0)
            {
                return (token, comparison);
            }
        }

        return null;
    }
}

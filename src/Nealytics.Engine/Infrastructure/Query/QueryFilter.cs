namespace Nealytics.Engine.Infrastructure.Query;

using System;

public enum FilterComparison
{
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

public readonly struct QueryFilter
{
    public string Column { get; init; }
    public string Value { get; init; }
    public FilterComparison Comparison { get; init; }
    public bool IsMeasure { get; init; }

    public static QueryFilter Equals(string column, string value) =>
        new() { Column = column, Value = value, Comparison = FilterComparison.Equal };

    public static string Operator(FilterComparison comparison) => comparison switch
    {
        FilterComparison.Equal => "=",
        FilterComparison.NotEqual => "!=",
        FilterComparison.Less => "<",
        FilterComparison.LessOrEqual => "<=",
        FilterComparison.Greater => ">",
        FilterComparison.GreaterOrEqual => ">=",
        _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "Unhandled comparison."),
    };
}

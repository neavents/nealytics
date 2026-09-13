namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Linq;
using Nealytics.Engine.Infrastructure.Configuration;

public static class FilterRejection
{
    public static string Message(
        in FilterParser.Result result, QueryColumns columns, MeasureRegistry? measures, int maxValueLength)
    {
        ArgumentNullException.ThrowIfNull(columns);

        switch (result.Outcome)
        {
            case FilterParser.Outcome.Malformed:
                return $"'filter' must be written column:value, or measure<op>number with <op> one of "
                    + $"=, !=, <, <=, >, >=. Got '{result.Offender}'.";
            case FilterParser.Outcome.UnknownColumn:
                return measures is null || measures.Active.Count == 0
                    ? columns.RejectionMessage("filter", result.Column)
                    : columns.RejectionMessage("filter", result.Column)
                        + $" Declared measures, for a comparison filter: "
                        + $"{string.Join(", ", measures.Active.Select(m => m.Name).Order(StringComparer.Ordinal))}.";
            case FilterParser.Outcome.ValueTooLong:
                return $"'filter' value for '{result.Column}' must not exceed {maxValueLength} characters.";
            case FilterParser.Outcome.NotANumber:
                return $"'filter' on measure '{result.Column}' must compare against a finite number. Got '{result.Offender}'.";
            default:
                return $"'filter' could not be parsed. Got '{result.Offender}'.";
        }
    }
}

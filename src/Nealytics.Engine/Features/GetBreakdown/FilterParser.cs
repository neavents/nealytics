namespace Nealytics.Engine.Features.GetBreakdown;

using System;
using System.Collections.Generic;

/// <summary>
/// Parses repeatable <c>filter=name:value</c> parameters into resolved column filters.
///
/// Shared rather than duplicated. <c>/breakdown</c> owned this outright and <c>/timeseries</c> had
/// no filtering at all, which meant "scans over time" could only ever be asked of a whole tenant —
/// so every per-menu, per-section and per-item chart in a dashboard either scanned the wrong scope
/// or could not be built. Two copies of the parsing would have been two chances to disagree about
/// what a filter means, and the one that decides which rows are counted is not the one anybody
/// reads.
///
/// <b>The injection boundary lives here.</b> The column that comes back is the allowlist's own
/// instance, never the caller's string — <c>filter=menu_id; DROP TABLE:x</c> does not resolve, so
/// it is a 400 and never reaches the SQL builder. The value is a bound parameter at the call site.
/// </summary>
public static class FilterParser
{
    public enum Outcome
    {
        Ok,
        Malformed,
        UnknownColumn,
        ValueTooLong,
    }

    public readonly struct Result
    {
        public Outcome Outcome { get; init; }
        public IReadOnlyList<BreakdownFilter> Filters { get; init; }

        /// <summary>The offending <c>name:value</c> pair, for the message.</summary>
        public string Offender { get; init; }

        /// <summary>The column name alone, when the column is what was wrong.</summary>
        public string Column { get; init; }
    }

    public static Result Parse(
        IReadOnlyList<string> raw, BreakdownColumns columns, int maxValueLength)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(columns);

        List<BreakdownFilter> filters = new(raw.Count);

        foreach (string entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            // name:value — split on the FIRST colon only, because a value may legitimately
            // contain one (a URL path, a timestamp).
            int separator = entry.IndexOf(':', StringComparison.Ordinal);

            if (separator <= 0)
            {
                return new Result { Outcome = Outcome.Malformed, Offender = entry, Filters = [] };
            }

            string name = entry[..separator];
            string value = entry[(separator + 1)..];

            if (!columns.TryResolve(name, out string column))
            {
                return new Result { Outcome = Outcome.UnknownColumn, Column = name, Filters = [] };
            }

            if (value.Length > maxValueLength)
            {
                return new Result { Outcome = Outcome.ValueTooLong, Column = name, Filters = [] };
            }

            filters.Add(new BreakdownFilter { Column = column, Value = value });
        }

        return new Result { Outcome = Outcome.Ok, Filters = filters };
    }
}

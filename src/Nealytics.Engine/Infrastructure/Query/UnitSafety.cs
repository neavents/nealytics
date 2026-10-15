namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Configuration;

public static class UnitSafety
{
    public static bool SplitsByUnit(
        Measure measure, string aggregation, string? groupByColumn, IReadOnlyList<QueryFilter>? filters)
    {
        ArgumentNullException.ThrowIfNull(measure);

        string? unit = measure.UnitDimension;

        return unit is not null
            && !string.Equals(aggregation, "count", StringComparison.OrdinalIgnoreCase)
            && string.Equals(groupByColumn, unit, StringComparison.Ordinal)
            && !FiltersOneUnit(unit, filters);
    }

    private static bool FiltersOneUnit(string unit, IReadOnlyList<QueryFilter>? filters)
    {
        if (filters is null)
        {
            return false;
        }

        foreach (QueryFilter filter in filters)
        {
            if (!filter.IsMeasure
                && filter.Comparison == FilterComparison.Equal
                && string.Equals(filter.Column, unit, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string? Violation(
        Measure measure, string aggregation, string? groupByColumn, IReadOnlyList<QueryFilter>? filters)
    {
        ArgumentNullException.ThrowIfNull(measure);

        string? unit = measure.UnitDimension;

        if (unit is null || string.Equals(aggregation, "count", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(groupByColumn, unit, StringComparison.Ordinal))
        {
            return null;
        }

        if (FiltersOneUnit(unit, filters))
        {
            return null;
        }

        return $"'{measure.Name}' is measured in units of '{unit}', so {aggregation}({measure.Name}) across "
            + $"different units would add unlike amounts. Group by '{unit}' or filter on one unit, such as "
            + $"filter={unit}:<value>.";
    }
}

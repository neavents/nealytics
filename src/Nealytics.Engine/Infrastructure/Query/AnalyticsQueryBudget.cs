namespace Nealytics.Engine.Infrastructure.Query;

using System.Collections.Generic;

internal static class AnalyticsQueryBudget
{
    internal static string? Violation(IReadOnlyList<QueryFilter> filters, IEnumerable<string>? groups)
    {
        int characters = 0;
        foreach (QueryFilter filter in filters)
        {
            characters += filter.Column.Length;
        }
        if (groups is not null)
        {
            foreach (string expression in groups)
            {
                characters += expression.Length;
            }
        }
        return characters > 32_768 ? "Analytics expression expansion exceeds the 32768-character request budget." : null;
    }
}

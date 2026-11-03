namespace Nealytics.Engine.Infrastructure.Query;

using System;

public sealed class AnalyticsColumns
{
    public AnalyticsColumns(QueryColumns columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        Value = columns;
    }

    public QueryColumns Value { get; }
}

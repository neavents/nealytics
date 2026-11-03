namespace Nealytics.Engine.Infrastructure.Query;

using System.Collections.Generic;
using System.Text;
using Nealytics.Engine.Infrastructure.Configuration;

internal static class MetricProjectionSql
{
    internal static void Append(
        StringBuilder sql, IReadOnlyList<NamedMetric> components,
        List<KeyValuePair<string, object?>> parameters, bool conditional)
    {
        if (components.Count == 0)
        {
            sql.Append("toString(count())");
        }
        for (int index = 0; index < components.Count; index++)
        {
            if (index != 0)
            {
                sql.Append(", ");
            }
            NamedMetric metric = components[index];
            int? conditionIndex = null;
            if (conditional && metric.EventType is string eventType)
            {
                conditionIndex = index;
                parameters.Add(new KeyValuePair<string, object?>(MetricAggregateSql.EventTypeParameter(index), eventType));
            }
            MetricAggregateSql.Append(sql, metric, conditionIndex);
        }
    }
}

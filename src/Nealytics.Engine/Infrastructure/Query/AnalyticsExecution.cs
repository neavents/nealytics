namespace Nealytics.Engine.Infrastructure.Query;

using System.Collections.Generic;
using System.Linq;
using Nealytics.Engine.Infrastructure.Configuration;

internal static class AnalyticsExecution
{
    internal static string? Violation(string? execution, string? accuracy, IEnumerable<NamedMetric> metrics)
    {
        if (execution is not (null or "" or "auto" or "raw"))
        {
            return "Execution must be auto or raw.";
        }
        if (accuracy is not (null or "" or "exact" or "approximate"))
        {
            return "Accuracy must be exact or approximate.";
        }
        if (accuracy != "approximate" && metrics.Any(metric => metric.Accuracy == "approximate"))
        {
            return "This definition contains approximate metrics; explicitly request accuracy=approximate.";
        }
        return null;
    }

    internal static string Accuracy(IEnumerable<NamedMetric> metrics) =>
        metrics.Any(metric => metric.Accuracy == "approximate") ? "approximate" : "exact";
}

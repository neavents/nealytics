namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Text.RegularExpressions;

public static class ClusterDdl
{
    private static readonly Regex NamePattern =
        new("^[A-Za-z0-9_{}.-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Clause(string? clusterName)
    {
        if (string.IsNullOrWhiteSpace(clusterName))
        {
            return string.Empty;
        }

        string trimmed = clusterName.Trim();

        if (!NamePattern.IsMatch(trimmed))
        {
            throw new InvalidOperationException(
                $"TelemetryEngine:ClusterName '{clusterName}' is not a valid cluster name. It may contain "
                + "letters, digits, '_', '.', '-' and a '{macro}', up to 128 characters.");
        }

        return " ON CLUSTER '" + trimmed + "'";
    }
}

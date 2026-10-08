namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

public sealed class TenantAttributeRegistry
{
    public const string Table = "tenant_attributes";
    public const string GroupPrefix = "tenant.";
    public const int MaxAttributes = 16;
    public const int MaxValueLength = 256;

    private static readonly Regex NamePattern =
        new("^[a-z][a-z0-9_]{0,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly FrozenSet<string> _names;

    public TenantAttributeRegistry(IOptions<TelemetryEngineOptions> options)
        : this(options.Value)
    {
    }

    public TenantAttributeRegistry(TelemetryEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<TenantAttributeOptions> configured = options.TenantAttributes ?? [];

        if (configured.Count > MaxAttributes)
        {
            throw new InvalidOperationException(
                $"TelemetryEngine:TenantAttributes declares {configured.Count} attributes; at most "
                + $"{MaxAttributes} are supported.");
        }

        List<string> ordered = new(configured.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int i = 0; i < configured.Count; i++)
        {
            string name = configured[i].Name?.Trim() ?? string.Empty;

            if (!NamePattern.IsMatch(name))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:TenantAttributes[{i}] has name '{configured[i].Name}', which is not "
                    + "valid. Names must match [a-z][a-z0-9_]{0,40}.");
            }

            if (!seen.Add(name))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:TenantAttributes[{i}] declares '{name}' a second time. Names must be unique.");
            }

            ordered.Add(name);
        }

        Declared = ordered;
        _names = ordered.ToFrozenSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Declared { get; }

    public bool Enabled => Declared.Count > 0;

    public bool IsDeclared(string? name) => name is not null && _names.Contains(name);

    public static string QualifiedTable => RollupRegistry.Database + "." + Table;

    public bool TryResolveGroupColumn(string? requested, out string attribute)
    {
        attribute = string.Empty;

        if (requested is null || !requested.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string candidate = requested[GroupPrefix.Length..];

        if (!_names.Contains(candidate))
        {
            return false;
        }

        attribute = candidate;
        return true;
    }

    public static bool IsGroupColumn(string column) =>
        column.StartsWith(GroupPrefix, StringComparison.Ordinal);

    public static string AttributeOf(string groupColumn) => groupColumn[GroupPrefix.Length..];

    public static string BuildTableDdl(string onCluster) =>
        "CREATE TABLE IF NOT EXISTS " + QualifiedTable + onCluster
        + " (project_id LowCardinality(String), attribute LowCardinality(String), tenant_id String, "
        + "value String, updated_at DateTime64(3, 'UTC')) "
        + "ENGINE = ReplacingMergeTree(updated_at) ORDER BY (project_id, attribute, tenant_id)";
}

namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Collections.Generic;
using System.Linq;
using Nealytics.Engine.Infrastructure.Configuration;

public sealed class QueryColumns
{
    private static readonly string[] CoreGroupable =
    [
        "event_type",
        "object_id",
        "traffic_class",
        "page_path",
        "referrer",
        "session_id",
        "user_id",
        "tenant_id",
        "device_class",
        "os",
        "browser",
        "country",
    ];

    private readonly Dictionary<string, string> _byName;
    private readonly TenantAttributeRegistry? _tenantAttributes;

    public QueryColumns(DimensionRegistry registry)
        : this(registry, null)
    {
    }

    public QueryColumns(DimensionRegistry registry, TenantAttributeRegistry? tenantAttributes)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _tenantAttributes = tenantAttributes;

        _byName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string column in CoreGroupable)
        {
            _byName[column] = column;
        }

        foreach (Dimension dimension in registry.Active)
        {
            _byName[dimension.Name] = dimension.Name;
        }

        Allowed = [.. _byName.Keys.OrderBy(name => name, StringComparer.Ordinal)];
        TenantGroupColumns = tenantAttributes is null
            ? []
            : [.. tenantAttributes.Declared.Select(name => TenantAttributeRegistry.GroupPrefix + name)];
    }

    public IReadOnlyList<string> Allowed { get; }

    public IReadOnlyList<string> TenantGroupColumns { get; }

    public bool TryResolveGroupBy(string? requested, out string column)
    {
        if (TryResolve(requested, out column))
        {
            return true;
        }

        if (_tenantAttributes is not null && _tenantAttributes.TryResolveGroupColumn(requested, out string attribute))
        {
            column = TenantAttributeRegistry.GroupPrefix + attribute;
            return true;
        }

        column = string.Empty;
        return false;
    }

    public string GroupByRejectionMessage(string parameterName, string? requested) =>
        TenantGroupColumns.Count == 0
            ? RejectionMessage(parameterName, requested)
            : $"'{parameterName}' must be one of: {string.Join(", ", Allowed.Concat(TenantGroupColumns))}. "
                + $"Got '{requested}', which is not a known column, an active dimension or a declared tenant attribute.";

    public bool TryResolve(string? requested, out string column)
    {
        if (requested is not null && _byName.TryGetValue(requested, out string? canonical))
        {
            column = canonical;
            return true;
        }

        column = string.Empty;
        return false;
    }

    public string RejectionMessage(string parameterName, string? requested) =>
        $"'{parameterName}' must be one of: {string.Join(", ", Allowed)}. "
        + $"Got '{requested}', which is not a known column or an active dimension.";
}

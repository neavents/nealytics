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

    public QueryColumns(DimensionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

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
    }

    public IReadOnlyList<string> Allowed { get; }

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

namespace Nealytics.Engine.Features.GetBreakdown;

using System;
using System.Collections.Generic;
using System.Linq;
using Nealytics.Engine.Infrastructure.Configuration;

/// <summary>
/// Which column names a caller may group or filter by.
///
/// <b>This is the injection boundary.</b> ClickHouse has no parameter form for an identifier, so a
/// group-by column has to reach the SQL as text. The rule that makes that safe is not escaping and
/// not sanitising: a caller-supplied string is never put into SQL at all. It is used only as a
/// dictionary key, and what goes into the statement is the canonical name this class already held.
/// <c>groupBy=menu_id; DROP TABLE</c> does not match a key, so it becomes a 400 and never reaches
/// the database.
///
/// The set is core columns plus <see cref="DimensionRegistry.Active"/>. Retired dimensions are
/// absent by construction, which is what "the query API stops offering it" means — and it comes
/// from the same registry the reconciler and ingest read, so the three cannot drift.
/// </summary>
public sealed class BreakdownColumns
{
    /// <summary>
    /// Core columns worth grouping by. <c>event_id</c> and <c>metadata_json</c> are excluded:
    /// grouping by a unique id returns one row per event, and grouping by a JSON blob is the thing
    /// typed columns exist to avoid.
    /// </summary>
    private static readonly string[] CoreGroupable =
    [
        "event_type",
        "object_id",
        "session_id",
        "user_id",
        "tenant_id",
        "device_class",
        "os",
        "browser",
        "country",
    ];

    private readonly Dictionary<string, string> _byName;

    public BreakdownColumns(DimensionRegistry registry)
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

    /// <summary>Every accepted name, sorted — used to make a rejection message actionable.</summary>
    public IReadOnlyList<string> Allowed { get; }

    /// <summary>
    /// Resolves a caller-supplied name to the canonical column.
    ///
    /// <paramref name="column"/> is deliberately the stored instance rather than
    /// <paramref name="requested"/>: the value that reaches the SQL builder has provably never been
    /// caller input, which is a stronger property than "we checked it".
    /// </summary>
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

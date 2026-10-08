namespace Nealytics.Engine.Infrastructure.Query;

using System.Collections.Generic;
using System.Text;

public static class IdentityStitching
{
    public const string AliasParameter = "aliasEventType";
    public const string StitchedColumn = "stitched_id";

    public static void AddParameter(List<KeyValuePair<string, object?>> parameters, string aliasEventType) =>
        parameters.Add(new KeyValuePair<string, object?>(AliasParameter, aliasEventType));

    public static void AppendLinks(StringBuilder sql, string? tenantId, TenantSet? set)
    {
        sql.Append("SELECT session_id AS anonymous_id, ");
        sql.Append("argMax(assumeNotNull(coalesce(user_id, object_id)), timestamp) AS identity_id ");
        sql.Append("FROM nealytics_core.global_events");
        ScopeClause.AppendProjectAndTenant(sql, tenantId, set);
        sql.Append(" AND event_type = {").Append(AliasParameter).Append(":String}");
        sql.Append(" AND session_id != '' AND (user_id IS NOT NULL OR object_id IS NOT NULL)");
        sql.Append(" GROUP BY anonymous_id");
    }

    public static void AppendAliasedSessions(StringBuilder sql, string? tenantId, TenantSet? set)
    {
        sql.Append("WITH links AS (");
        AppendLinks(sql, tenantId, set);
        sql.Append(") SELECT anonymous_id FROM links WHERE identity_id = {userId:String}");
        sql.Append(" UNION ALL SELECT hop1.anonymous_id FROM links AS hop1 INNER JOIN links AS hop2");
        sql.Append(" ON hop1.identity_id = hop2.anonymous_id WHERE hop2.identity_id = {userId:String}");
    }

    public static void AppendStitchedSource(StringBuilder sql, string? tenantId, TenantSet? set, string where)
    {
        sql.Append("(SELECT *, multiIf(ifNull(user_id, '') != '', ifNull(user_id, ''), ");
        sql.Append("hop2.identity_id != '', hop2.identity_id, hop1.identity_id != '', hop1.identity_id, ");
        sql.Append("session_id) AS ").Append(StitchedColumn);
        sql.Append(" FROM nealytics_core.global_events LEFT ANY JOIN (");
        AppendLinks(sql, tenantId, set);
        sql.Append(") AS hop1 ON session_id = hop1.anonymous_id LEFT ANY JOIN (");
        AppendLinks(sql, tenantId, set);
        sql.Append(") AS hop2 ON hop1.identity_id = hop2.anonymous_id");
        sql.Append(where);
        sql.Append(')');
    }
}

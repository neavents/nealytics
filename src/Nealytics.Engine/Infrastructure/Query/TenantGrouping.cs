namespace Nealytics.Engine.Infrastructure.Query;

using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Configuration;

public static class TenantGrouping
{
    public const string MapAlias = "tenant_attribute_map";
    public const string AttributeParameter = "groupAttribute";

    public static string Prelude =>
        "(SELECT (groupArray(tenant_id), groupArray(value)) FROM " + TenantAttributeRegistry.QualifiedTable
        + " FINAL WHERE project_id = {projectId:String} AND attribute = {" + AttributeParameter + ":String}) AS "
        + MapAlias;

    public static string MappedKey =>
        "transform(tenant_id, " + MapAlias + ".1, " + MapAlias + ".2, '')";

    public static bool NeedsPrelude(string column, Rollup? rollup) =>
        TenantAttributeRegistry.IsGroupColumn(column) && rollup is not { IsGroupRollup: true };

    public static void AddParameter(List<KeyValuePair<string, object?>> parameters, string column, Rollup? rollup)
    {
        if (NeedsPrelude(column, rollup))
        {
            parameters.Add(new KeyValuePair<string, object?>(
                AttributeParameter, TenantAttributeRegistry.AttributeOf(column)));
        }
    }

    public static string RawKey(string column) =>
        TenantAttributeRegistry.IsGroupColumn(column) ? MappedKey : "ifNull(toString(" + column + "), '')";

    public static string RollupKey(string column, Rollup rollup)
    {
        if (!TenantAttributeRegistry.IsGroupColumn(column))
        {
            return column;
        }

        return rollup.IsGroupRollup
            ? RollupRegistry.TenantAttributeColumn(TenantAttributeRegistry.AttributeOf(column))
            : MappedKey;
    }

    public static string With(string column, Rollup? rollup) =>
        NeedsPrelude(column, rollup) ? "WITH " + Prelude + ", " : "WITH ";
}

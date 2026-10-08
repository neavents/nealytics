namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Nealytics.Engine.Infrastructure.Configuration;

public readonly struct TenantSet
{
    public string Attribute { get; init; }
    public string Value { get; init; }

    public string Describe() => Attribute + ":" + Value;
}

public readonly struct QueryScope
{
    public string ProjectId { get; init; }
    public string TenantId { get; init; }
    public TenantSet? TenantSet { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string? TrafficClass { get; init; }
    public string? EventType { get; init; }
    public IReadOnlyList<QueryFilter> Filters { get; init; }
}

public static class ScopeClause
{
    public static List<KeyValuePair<string, object?>> Parameters(in QueryScope scope, int capacity = 8)
    {
        List<KeyValuePair<string, object?>> parameters = new(capacity)
        {
            new KeyValuePair<string, object?>("projectId", scope.ProjectId),
            new KeyValuePair<string, object?>("tenantId", scope.TenantId),
            new KeyValuePair<string, object?>("fromTimestamp", scope.From),
            new KeyValuePair<string, object?>("toTimestamp", scope.To),
        };

        if (scope.TrafficClass is not null)
        {
            parameters.Add(new KeyValuePair<string, object?>("trafficClass", scope.TrafficClass));
        }

        AddTenantSetParameters(parameters, scope.TenantSet);

        if (!string.IsNullOrEmpty(scope.EventType))
        {
            parameters.Add(new KeyValuePair<string, object?>("eventType", scope.EventType));
        }

        IReadOnlyList<QueryFilter> filters = scope.Filters ?? [];

        for (int i = 0; i < filters.Count; i++)
        {
            QueryFilter filter = filters[i];
            object value = filter.IsMeasure
                ? double.Parse(filter.Value, NumberStyles.Float, CultureInfo.InvariantCulture)
                : filter.Value;
            parameters.Add(new KeyValuePair<string, object?>(FilterParameter(i), value));
        }

        return parameters;
    }

    public static void AddTenantSetParameters(List<KeyValuePair<string, object?>> parameters, TenantSet? set)
    {
        if (set is TenantSet members)
        {
            parameters.Add(new KeyValuePair<string, object?>("tenantSetAttribute", members.Attribute));
            parameters.Add(new KeyValuePair<string, object?>("tenantSetValue", members.Value));
        }
    }

    public static string TenantMembers =>
        "SELECT tenant_id FROM " + TenantAttributeRegistry.QualifiedTable
        + " FINAL WHERE project_id = {projectId:String} AND attribute = {tenantSetAttribute:String}"
        + " AND value = {tenantSetValue:String}";

    public static void AppendProjectAndTenant(StringBuilder sql, string? tenantId, TenantSet? set)
    {
        sql.Append(" WHERE project_id = {projectId:String} AND ");
        AppendTenant(sql, tenantId, set);
    }

    public static void AppendTenant(StringBuilder sql, string? tenantId, TenantSet? set)
    {
        if (set is null)
        {
            sql.Append("tenant_id = {tenantId:String}");
            return;
        }

        if (!string.IsNullOrEmpty(tenantId))
        {
            sql.Append("tenant_id = {tenantId:String} AND ");
        }

        sql.Append("tenant_id IN (").Append(TenantMembers).Append(')');
    }

    public static void AppendGroupRollupTenant(StringBuilder sql, TenantSet set)
    {
        sql.Append(" WHERE project_id = {projectId:String} AND ")
            .Append(RollupRegistry.TenantAttributeColumn(set.Attribute))
            .Append(" = {tenantSetValue:String}");
    }

    public static void AppendRaw(StringBuilder sql, in QueryScope scope)
    {
        AppendProjectAndTenant(sql, scope.TenantId, scope.TenantSet);
        sql.Append(" AND timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64}");
        sql.Append(TrafficFilter.Clause(scope.TrafficClass));

        if (!string.IsNullOrEmpty(scope.EventType))
        {
            sql.Append(" AND event_type = {eventType:String}");
        }

        AppendFilters(sql, scope.Filters, normalise: true);
    }

    public static void AppendRollup(StringBuilder sql, in QueryScope scope, string bucketColumn) =>
        AppendRollup(sql, scope, bucketColumn, null);

    public static void AppendRollup(StringBuilder sql, in QueryScope scope, string bucketColumn, Rollup? rollup)
    {
        if (rollup is { IsGroupRollup: true } && scope.TenantSet is TenantSet set)
        {
            AppendGroupRollupTenant(sql, set);
        }
        else
        {
            AppendProjectAndTenant(sql, scope.TenantId, scope.TenantSet);
        }

        sql.Append(" AND ").Append(bucketColumn).Append(" >= {fromTimestamp:DateTime64} AND ")
            .Append(bucketColumn).Append(" < {toTimestamp:DateTime64}");
        sql.Append(TrafficFilter.Clause(scope.TrafficClass));

        if (!string.IsNullOrEmpty(scope.EventType))
        {
            sql.Append(" AND event_type = {eventType:String}");
        }

        AppendFilters(sql, scope.Filters, normalise: false);
    }

    public static bool HasMeasureFilter(IReadOnlyList<QueryFilter>? filters)
    {
        if (filters is null)
        {
            return false;
        }

        foreach (QueryFilter filter in filters)
        {
            if (filter.IsMeasure)
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendFilters(StringBuilder sql, IReadOnlyList<QueryFilter>? filters, bool normalise)
    {
        if (filters is null)
        {
            return;
        }

        for (int i = 0; i < filters.Count; i++)
        {
            QueryFilter filter = filters[i];
            sql.Append(" AND ");

            if (filter.IsMeasure)
            {
                sql.Append(filter.Column).Append(' ').Append(QueryFilter.Operator(filter.Comparison))
                    .Append(" {").Append(FilterParameter(i)).Append(":Float64}");
                continue;
            }

            if (normalise)
            {
                sql.Append("toString(").Append(filter.Column).Append(')');
            }
            else
            {
                sql.Append(filter.Column);
            }

            sql.Append(" = {").Append(FilterParameter(i)).Append(":String}");
        }
    }

    private static string FilterParameter(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"filter{index}");
}

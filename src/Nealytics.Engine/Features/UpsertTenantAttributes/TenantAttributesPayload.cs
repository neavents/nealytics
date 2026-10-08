namespace Nealytics.Engine.Features.UpsertTenantAttributes;

using System.Collections.Generic;

public sealed class TenantAttributesPayload
{
    public string ProjectId { get; init; } = string.Empty;

    public List<TenantAttributesEntry>? Tenants { get; init; }
}

public sealed class TenantAttributesEntry
{
    public string TenantId { get; init; } = string.Empty;

    public Dictionary<string, string?>? Attributes { get; init; }
}

public sealed class TenantAttributesResponse
{
    public int Tenants { get; init; }

    public int Values { get; init; }
}

public readonly struct TenantAttributeRow
{
    public string TenantId { get; init; }
    public string Attribute { get; init; }
    public string Value { get; init; }
}

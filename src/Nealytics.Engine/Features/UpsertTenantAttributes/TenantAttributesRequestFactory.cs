namespace Nealytics.Engine.Features.UpsertTenantAttributes;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Configuration;

public readonly struct TenantAttributesRequestResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string ProjectId { get; init; }
    public int TenantCount { get; init; }
    public IReadOnlyList<TenantAttributeRow> Rows { get; init; }

    public static TenantAttributesRequestResult Fail(string message) => new() { Success = false, ErrorMessage = message, Rows = [] };
}

public static class TenantAttributesRequestFactory
{
    public const int MaxIdentifierLength = 256;

    public static TenantAttributesRequestResult Create(
        TenantAttributesPayload? payload, TenantAttributeRegistry registry, int maxTenants)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (!registry.Enabled)
        {
            return TenantAttributesRequestResult.Fail(
                "This deployment declares no tenant attributes under TelemetryEngine:TenantAttributes.");
        }

        if (payload is null || string.IsNullOrEmpty(payload.ProjectId))
        {
            return TenantAttributesRequestResult.Fail("'projectId' is required.");
        }

        if (payload.ProjectId.Length > MaxIdentifierLength)
        {
            return TenantAttributesRequestResult.Fail($"'projectId' must not exceed {MaxIdentifierLength} characters.");
        }

        List<TenantAttributesEntry> tenants = payload.Tenants ?? [];

        if (tenants.Count == 0)
        {
            return TenantAttributesRequestResult.Fail("'tenants' must list at least one tenant.");
        }

        if (tenants.Count > maxTenants)
        {
            return TenantAttributesRequestResult.Fail(
                $"At most {maxTenants} tenants are accepted per request; split the upload.");
        }

        List<TenantAttributeRow> rows = new(tenants.Count * Math.Max(1, registry.Declared.Count));
        HashSet<string> seenTenants = new(StringComparer.Ordinal);

        foreach (TenantAttributesEntry entry in tenants)
        {
            if (entry is null || string.IsNullOrEmpty(entry.TenantId) || entry.TenantId.Length > MaxIdentifierLength)
            {
                return TenantAttributesRequestResult.Fail(
                    $"Every tenant needs a 'tenantId' of 1 to {MaxIdentifierLength} characters.");
            }

            if (!seenTenants.Add(entry.TenantId))
            {
                return TenantAttributesRequestResult.Fail(
                    $"Tenant '{entry.TenantId}' is listed twice; the later entry would silently win.");
            }

            if (entry.Attributes is null || entry.Attributes.Count == 0)
            {
                return TenantAttributesRequestResult.Fail(
                    $"Tenant '{entry.TenantId}' sets no attributes.");
            }

            foreach (KeyValuePair<string, string?> attribute in entry.Attributes)
            {
                if (!registry.IsDeclared(attribute.Key))
                {
                    return TenantAttributesRequestResult.Fail(
                        $"'{attribute.Key}' is not a declared tenant attribute. Declared: "
                        + $"{string.Join(", ", registry.Declared)}.");
                }

                string value = attribute.Value ?? string.Empty;

                if (value.Length > TenantAttributeRegistry.MaxValueLength)
                {
                    return TenantAttributesRequestResult.Fail(
                        $"The value of '{attribute.Key}' for tenant '{entry.TenantId}' exceeds "
                        + $"{TenantAttributeRegistry.MaxValueLength} characters.");
                }

                rows.Add(new TenantAttributeRow { TenantId = entry.TenantId, Attribute = attribute.Key, Value = value });
            }
        }

        return new TenantAttributesRequestResult
        {
            Success = true,
            ProjectId = payload.ProjectId,
            TenantCount = tenants.Count,
            Rows = rows,
        };
    }
}

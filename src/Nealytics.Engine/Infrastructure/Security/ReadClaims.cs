namespace Nealytics.Engine.Infrastructure.Security;

using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public enum ReadIdentityOutcome
{
    Ok,
    Forbidden,
    BadRequest,
}

public readonly struct ReadIdentity
{
    public ReadIdentityOutcome Outcome { get; init; }
    public string? ProjectId { get; init; }
    public string? TenantId { get; init; }
    public TenantSet? TenantSet { get; init; }
    public string? ErrorMessage { get; init; }

    public bool Rejected => Outcome != ReadIdentityOutcome.Ok;

    public IResult Rejection() => Outcome == ReadIdentityOutcome.BadRequest
        ? Results.BadRequest(ErrorMessage)
        : Results.Forbid();
}

public static class ReadClaims
{
    public const string ProjectClaim = "project_id";
    public const string TenantClaim = "tenant_id";
    public const string TenantSetClaim = "tenant_set";
    public const string TenantParameter = "tenant";
    public const int MaxFieldLength = 256;

    public static ReadIdentity Resolve(HttpContext context, TenantAttributeRegistry tenantAttributes)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Resolve(context.User, context.Request.Query[TenantParameter].ToString(), tenantAttributes);
    }

    public static ReadIdentity Resolve(
        ClaimsPrincipal user, string? requestedTenant, TenantAttributeRegistry tenantAttributes)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(tenantAttributes);

        string? projectId = user.FindFirst(ProjectClaim)?.Value;
        string? tenantId = user.FindFirst(TenantClaim)?.Value;
        string? setClaim = user.FindFirst(TenantSetClaim)?.Value;

        if (string.IsNullOrEmpty(setClaim))
        {
            return new ReadIdentity { ProjectId = projectId, TenantId = tenantId };
        }

        if (!string.IsNullOrEmpty(tenantId))
        {
            return Forbidden();
        }

        int colon = setClaim.IndexOf(':', StringComparison.Ordinal);

        if (colon <= 0 || colon == setClaim.Length - 1)
        {
            return Forbidden();
        }

        string attribute = setClaim[..colon];
        string value = setClaim[(colon + 1)..];

        if (!tenantAttributes.IsDeclared(attribute) || value.Length > TenantAttributeRegistry.MaxValueLength)
        {
            return Forbidden();
        }

        string narrowed = string.Empty;

        if (!string.IsNullOrEmpty(requestedTenant))
        {
            if (requestedTenant.Length > MaxFieldLength)
            {
                return new ReadIdentity
                {
                    Outcome = ReadIdentityOutcome.BadRequest,
                    ErrorMessage = $"'{TenantParameter}' must not exceed {MaxFieldLength} characters.",
                };
            }

            narrowed = requestedTenant;
        }

        return new ReadIdentity
        {
            ProjectId = projectId,
            TenantId = narrowed,
            TenantSet = new TenantSet { Attribute = attribute, Value = value },
        };
    }

    private static ReadIdentity Forbidden() => new() { Outcome = ReadIdentityOutcome.Forbidden };
}

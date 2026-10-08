namespace Nealytics.Engine.Infrastructure.Security;

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;

public static class ReadScopeFilter
{
    public const string Everything = "*";

    public static TBuilder RequireReadScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) =>
        {
            HttpContext context = invocation.HttpContext;
            string claimType = context.RequestServices
                .GetRequiredService<IOptions<TelemetryEngineOptions>>().Value.ReadScopeClaim;

            if (!string.IsNullOrEmpty(claimType) && !Grants(context.User, claimType, scope))
            {
                return Results.Forbid();
            }

            return await next(invocation);
        });

        return builder;
    }

    public static bool Grants(ClaimsPrincipal user, string claimType, string scope)
    {
        ArgumentNullException.ThrowIfNull(user);

        foreach (Claim claim in user.FindAll(claimType))
        {
            foreach (string granted in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(granted, scope, StringComparison.Ordinal)
                    || string.Equals(granted, Everything, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

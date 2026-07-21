namespace Nealytics.Engine.Features.GetActiveUsers;

using System;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;

public static class GetActiveUsersEndpoint
{
    public static void MapGetActiveUsers(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/active", async (
            HttpContext context,
            GetActiveUsersQuery query,
            IOptions<TelemetryEngineOptions> options,
            CancellationToken cancellationToken) =>
        {
            ClaimsPrincipal user = context.User;
            TelemetryEngineOptions engineOptions = options.Value;

            ActiveUsersRequestResult parsed = ActiveUsersRequestFactory.Create(
                user.FindFirst("project_id")?.Value,
                user.FindFirst("tenant_id")?.Value,
                context.Request.Query["limit"].ToString(),
                context.Request.Query["interval"].ToString(),
                context.Request.Query["by"].ToString(),
                context.Request.Query["mode"].ToString(),
                context.Request.Query["from"].ToString(),
                context.Request.Query["to"].ToString(),
                engineOptions.MaxQueryLimit,
                engineOptions.DefaultSessionQueryRangeHours,
                DateTime.UtcNow);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == ActiveUsersRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            ActiveUsersResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);

            return Results.Ok(response);
        })
        .WithName("GetActiveUsers")
        .Produces<ActiveUsersResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

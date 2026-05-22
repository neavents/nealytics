namespace Nealytics.Engine.Features.GetProjectTimeline;

using System;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Nealytics.Engine.Infrastructure.Configuration;

public static class GetProjectTimelineEndpoint
{
    public static void MapGetProjectTimeline(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/telemetry/timeline", async (
            HttpContext context,
            GetProjectTimelineQuery query,
            IOptions<TelemetryEngineOptions> options,
            CancellationToken cancellationToken) =>
        {
            ClaimsPrincipal user = context.User;
            string? tokenProjectId = user.FindFirst("project_id")?.Value;
            string? tokenTenantId = user.FindFirst("tenant_id")?.Value;

            if (string.IsNullOrWhiteSpace(tokenProjectId) || string.IsNullOrWhiteSpace(tokenTenantId))
            {
                return Results.Forbid();
            }

            int maxLimit = options.Value.MaxQueryLimit;
            int limit = 100;

            if (context.Request.Query.TryGetValue("limit", out StringValues limitValues)
                && int.TryParse(limitValues[0], out int parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 1, maxLimit);
            }

            ProjectTimelineResponse response =
                await query.ExecuteAsync(tokenProjectId, tokenTenantId, limit, cancellationToken);

            return Results.Ok(response);
        })
        .WithName("GetProjectTimeline")
        .Produces<ProjectTimelineResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

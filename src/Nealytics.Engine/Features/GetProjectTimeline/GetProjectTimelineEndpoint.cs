namespace Nealytics.Engine.Features.GetProjectTimeline;

using System;
using System.Globalization;
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

            if (tokenProjectId.Length > 256 || tokenTenantId.Length > 256)
            {
                return Results.BadRequest("Project ID and Tenant ID must not exceed 256 characters.");
            }

            int maxLimit = options.Value.MaxQueryLimit;
            int limit = 100;

            if (context.Request.Query.TryGetValue("limit", out StringValues limitValues)
                && int.TryParse(limitValues[0], out int parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 1, maxLimit);
            }

            DateTime? cursor = null;
            if (context.Request.Query.TryGetValue("before", out StringValues beforeValues)
                && DateTime.TryParse(beforeValues[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime parsedCursor))
            {
                cursor = parsedCursor;
            }

            ProjectTimelineResponse response =
                await query.ExecuteAsync(tokenProjectId, tokenTenantId, limit, cursor, cancellationToken);

            return Results.Ok(response);
        })
        .WithName("GetProjectTimeline")
        .Produces<ProjectTimelineResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

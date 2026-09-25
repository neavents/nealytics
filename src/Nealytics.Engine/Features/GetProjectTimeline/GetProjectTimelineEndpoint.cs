namespace Nealytics.Engine.Features.GetProjectTimeline;

using System;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public static class GetProjectTimelineEndpoint
{
    public static void MapGetProjectTimeline(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/telemetry/timeline", async (
            HttpContext context,
            GetProjectTimelineQuery query,
            QueryGuard guard,
            IOptions<TelemetryEngineOptions> options,
            CancellationToken cancellationToken) =>
        {
            ClaimsPrincipal user = context.User;

            TimelineRequestResult parsed = TimelineRequestFactory.Create(
                user.FindFirst("project_id")?.Value,
                user.FindFirst("tenant_id")?.Value,
                context.Request.Query["limit"].ToString(),
                context.Request.Query["before"].ToString(),
                context.Request.Query["eventType"].ToString(),
                context.Request.Query["sessionId"].ToString(),
                context.Request.Query["objectId"].ToString(),
                context.Request.Query["metaKey"].ToString(),
                context.Request.Query["metaValue"].ToString(),
                options.Value.MaxQueryLimit,
                guard.MaxRange,
                DateTime.UtcNow);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == TimelineRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            ProjectTimelineResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);

            return Results.Ok(response);
        })
        .WithName("GetProjectTimeline")
        .Produces<ProjectTimelineResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

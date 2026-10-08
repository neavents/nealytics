namespace Nealytics.Engine.Features.GetProjectTimeline;

using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;

public static class GetProjectTimelineEndpoint
{
    public static void MapGetProjectTimeline(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/telemetry/timeline", async (
            HttpContext context,
            GetProjectTimelineQuery query,
            IOptions<TelemetryEngineOptions> options,
            TenantAttributeRegistry tenantAttributes,
            CancellationToken cancellationToken) =>
        {
            ReadIdentity identity = ReadClaims.Resolve(context, tenantAttributes);

            if (identity.Rejected)
            {
                return identity.Rejection();
            }

            TimelineRequestResult parsed = TimelineRequestFactory.Create(
                identity.ProjectId,
                identity.TenantId,
                context.Request.Query["limit"].ToString(),
                context.Request.Query["before"].ToString(),
                context.Request.Query["eventType"].ToString(),
                context.Request.Query["sessionId"].ToString(),
                context.Request.Query["objectId"].ToString(),
                context.Request.Query["metaKey"].ToString(),
                context.Request.Query["metaValue"].ToString(),
                options.Value.MaxQueryLimit,
                context.Request.Query["userId"].ToString(),
                context.Request.Query["stitched"].ToString(),
                options.Value.AliasEventType,
                identity.TenantSet);

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
        .RequireAuthorization()
        .RequireReadScope("timeline");
    }
}

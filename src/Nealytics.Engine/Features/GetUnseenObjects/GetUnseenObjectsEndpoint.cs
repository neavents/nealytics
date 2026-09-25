namespace Nealytics.Engine.Features.GetUnseenObjects;

using System;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public static class GetUnseenObjectsEndpoint
{
    public static void MapGetUnseenObjects(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/analytics/unseen", async (
            HttpContext context,
            UnseenObjectsBody? body,
            GetUnseenObjectsQuery query,
            QueryColumns columns,
            MeasureRegistry measures,
            IOptions<TelemetryEngineOptions> options,
            QueryGuard guard,
            CancellationToken cancellationToken) =>
        {
            ClaimsPrincipal user = context.User;

            UnseenObjectsRequestResult parsed = UnseenObjectsRequestFactory.Create(
                user.FindFirst("project_id")?.Value,
                user.FindFirst("tenant_id")?.Value,
                body,
                columns,
                measures,
                options.Value.DefaultSessionQueryRangeHours,
                DateTime.UtcNow);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == UnseenObjectsRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            if (!guard.Admits(parsed.Request.From, parsed.Request.To))
            {
                return guard.RejectRange(parsed.Request.From, parsed.Request.To);
            }

            UnseenObjectsResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);
            return Results.Ok(response);
        })
        .WithName("GetUnseenObjects")
        .Accepts<UnseenObjectsBody>("application/json")
        .Produces<UnseenObjectsResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

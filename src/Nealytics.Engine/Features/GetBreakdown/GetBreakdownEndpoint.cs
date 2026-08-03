namespace Nealytics.Engine.Features.GetBreakdown;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;

public static class GetBreakdownEndpoint
{
    public static void MapGetBreakdown(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/breakdown", async (
            HttpContext context,
            GetBreakdownQuery query,
            BreakdownColumns columns,
            IOptions<TelemetryEngineOptions> options,
            CancellationToken cancellationToken) =>
        {
            ClaimsPrincipal user = context.User;
            TelemetryEngineOptions engineOptions = options.Value;

            BreakdownRequestResult parsed = BreakdownRequestFactory.Create(
                user.FindFirst("project_id")?.Value,
                user.FindFirst("tenant_id")?.Value,
                context.Request.Query["metric"].ToString(),
                context.Request.Query["groupBy"].ToString(),
                context.Request.Query["eventType"].ToString(),
                // Repeatable: ?filter=country:TR&filter=device_class:mobile
                context.Request.Query["filter"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                context.Request.Query["from"].ToString(),
                context.Request.Query["to"].ToString(),
                context.Request.Query["limit"].ToString(),
                context.Request.Query["orderBy"].ToString(),
                columns,
                engineOptions.MaxQueryLimit,
                engineOptions.DefaultSessionQueryRangeHours,
                DateTime.UtcNow);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == BreakdownRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            BreakdownResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);

            return Results.Ok(response);
        })
        .WithName("GetBreakdown")
        .Produces<BreakdownResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

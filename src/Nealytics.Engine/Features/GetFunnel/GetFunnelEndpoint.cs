namespace Nealytics.Engine.Features.GetFunnel;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public static class GetFunnelEndpoint
{
    public static void MapGetFunnel(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/funnel", async (
            HttpContext context,
            GetFunnelQuery query,
            QueryColumns columns,
            MeasureRegistry measures,
            IOptions<TelemetryEngineOptions> options,
            CancellationToken cancellationToken) =>
        {
            ClaimsPrincipal user = context.User;
            TelemetryEngineOptions engineOptions = options.Value;

            FunnelRequestResult parsed = FunnelRequestFactory.Create(
                user.FindFirst("project_id")?.Value,
                user.FindFirst("tenant_id")?.Value,
                context.Request.Query["step"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                context.Request.Query["grain"].ToString(),
                context.Request.Query["breakdownBy"].ToString(),
                context.Request.Query["windowSeconds"].ToString(),
                context.Request.Query["from"].ToString(),
                context.Request.Query["to"].ToString(),
                context.Request.Query["limit"].ToString(),
                columns,
                measures,
                engineOptions.MaxQueryLimit,
                engineOptions.DefaultSessionQueryRangeHours,
                DateTime.UtcNow);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == FunnelRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            FunnelResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);

            return Results.Ok(response);
        })
        .WithName("GetFunnel")
        .Produces<FunnelResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

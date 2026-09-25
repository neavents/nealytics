namespace Nealytics.Engine.Features.GetPivot;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

public static class GetPivotEndpoint
{
    public static void MapGetPivot(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/pivot", async (
            HttpContext context,
            GetPivotQuery query,
            QueryColumns columns,
            MeasureRegistry measures,
            IOptions<TelemetryEngineOptions> options,
            QueryGuard guard,
            CancellationToken cancellationToken) =>
        {
            ClaimsPrincipal user = context.User;
            TelemetryEngineOptions engineOptions = options.Value;

            PivotRequestResult parsed = PivotRequestFactory.Create(
                user.FindFirst("project_id")?.Value,
                user.FindFirst("tenant_id")?.Value,
                context.Request.Query["groupBy"].ToString(),
                context.Request.Query["metric"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                context.Request.Query["filter"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                context.Request.Query["from"].ToString(),
                context.Request.Query["to"].ToString(),
                context.Request.Query["limit"].ToString(),
                context.Request.Query["orderBy"].ToString(),
                context.Request.Query["order"].ToString(),
                context.Request.Query["traffic"].ToString(),
                context.Request.Query["mode"].ToString(),
                context.Request.Query["exact"].ToString(),
                columns,
                measures,
                engineOptions.MaxQueryLimit,
                engineOptions.DefaultSessionQueryRangeHours,
                DateTime.UtcNow);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == PivotRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            if (!guard.Admits(parsed.Request.From, parsed.Request.To))
            {
                return guard.RejectRange(parsed.Request.From, parsed.Request.To);
            }

            PivotResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);
            return Results.Ok(response);
        })
        .WithName("GetPivot")
        .Produces<PivotResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

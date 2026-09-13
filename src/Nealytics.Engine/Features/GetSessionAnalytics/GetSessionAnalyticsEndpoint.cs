namespace Nealytics.Engine.Features.GetSessionAnalytics;

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

public static class GetSessionAnalyticsEndpoint
{
    public static void MapGetSessionAnalytics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/sessions", async (
            HttpContext context,
            GetSessionAnalyticsQuery query,
            QueryColumns columns,
            MeasureRegistry measures,
            IOptions<TelemetryEngineOptions> options,
            CancellationToken cancellationToken) =>
        {
            ClaimsPrincipal user = context.User;
            TelemetryEngineOptions engineOptions = options.Value;

            SessionAnalyticsRequestResult parsed = SessionAnalyticsRequestFactory.Create(
                user.FindFirst("project_id")?.Value,
                user.FindFirst("tenant_id")?.Value,
                context.Request.Query["limit"].ToString(),
                context.Request.Query["from"].ToString(),
                context.Request.Query["to"].ToString(),
                context.Request.Query["traffic"].ToString(),
                context.Request.Query["filter"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                columns,
                measures,
                engineOptions.MaxQueryLimit,
                engineOptions.DefaultSessionQueryRangeHours,
                DateTime.UtcNow);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == SessionAnalyticsRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            SessionAnalyticsResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);

            return Results.Ok(response);
        })
        .WithName("GetSessionAnalytics")
        .Produces<SessionAnalyticsResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

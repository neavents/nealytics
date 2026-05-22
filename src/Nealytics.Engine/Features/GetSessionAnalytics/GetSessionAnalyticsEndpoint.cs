namespace Nealytics.Engine.Features.GetSessionAnalytics;

using System;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Nealytics.Engine.Infrastructure.Configuration;

public static class GetSessionAnalyticsEndpoint
{
    public static void MapGetSessionAnalytics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/sessions", async (
            HttpContext context,
            GetSessionAnalyticsQuery query,
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

            TelemetryEngineOptions engineOptions = options.Value;
            int limit = 100;

            if (context.Request.Query.TryGetValue("limit", out StringValues limitValues)
                && int.TryParse(limitValues[0], out int parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 1, engineOptions.MaxQueryLimit);
            }

            DateTime now = DateTime.UtcNow;
            DateTime fromUtc = now.AddHours(-engineOptions.DefaultSessionQueryRangeHours);
            DateTime toUtc = now;

            StringValues fromValues = context.Request.Query["from"];
            if (fromValues.Count > 0 && DateTime.TryParse(fromValues[0], out DateTime parsedFrom))
            {
                fromUtc = parsedFrom;
            }

            StringValues toValues = context.Request.Query["to"];
            if (toValues.Count > 0 && DateTime.TryParse(toValues[0], out DateTime parsedTo))
            {
                toUtc = parsedTo;
            }

            SessionAnalyticsResponse response =
                await query.ExecuteAsync(tokenProjectId, tokenTenantId, fromUtc, toUtc, limit, cancellationToken);

            return Results.Ok(response);
        })
        .WithName("GetSessionAnalytics")
        .Produces<SessionAnalyticsResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

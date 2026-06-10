namespace Nealytics.Engine.Features.GetSessionAnalytics;

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

            if (tokenProjectId.Length > 256 || tokenTenantId.Length > 256)
            {
                return Results.BadRequest("Project ID and Tenant ID must not exceed 256 characters.");
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
            if (fromValues.Count > 0 && DateTime.TryParse(fromValues[0], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTime parsedFrom))
            {
                fromUtc = parsedFrom;
            }

            StringValues toValues = context.Request.Query["to"];
            if (toValues.Count > 0 && DateTime.TryParse(toValues[0], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTime parsedTo))
            {
                toUtc = parsedTo;
            }

            if (fromUtc > toUtc)
            {
                return Results.BadRequest("'from' must be before or equal to 'to'.");
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

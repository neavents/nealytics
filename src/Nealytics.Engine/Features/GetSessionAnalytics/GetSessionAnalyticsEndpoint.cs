namespace Nealytics.Engine.Features.GetSessionAnalytics;

using System;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
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
            TenantAttributeRegistry tenantAttributes,
            CancellationToken cancellationToken) =>
        {
            ReadIdentity identity = ReadClaims.Resolve(context, tenantAttributes);

            if (identity.Rejected)
            {
                return identity.Rejection();
            }

            TelemetryEngineOptions engineOptions = options.Value;

            SessionAnalyticsRequestResult parsed = SessionAnalyticsRequestFactory.Create(
                identity.ProjectId,
                identity.TenantId,
                context.Request.Query["limit"].ToString(),
                context.Request.Query["from"].ToString(),
                context.Request.Query["to"].ToString(),
                context.Request.Query["traffic"].ToString(),
                context.Request.Query["filter"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                columns,
                measures,
                engineOptions.MaxQueryLimit,
                engineOptions.DefaultSessionQueryRangeHours,
                DateTime.UtcNow,
                identity.TenantSet);

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
        .RequireAuthorization()
        .RequireReadScope("sessions");
    }
}

namespace Nealytics.Engine.Features.GetTopEvents;

using System;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Query;

public static class GetTopEventsEndpoint
{
    public static void MapGetTopEvents(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/top", async (
            HttpContext context,
            GetTopEventsQuery query,
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

            TopEventsRequestResult parsed = TopEventsRequestFactory.Create(
                identity.ProjectId,
                identity.TenantId,
                context.Request.Query["limit"].ToString(),
                context.Request.Query["dimension"].ToString(),
                columns,
                measures,
                context.Request.Query["from"].ToString(),
                context.Request.Query["to"].ToString(),
                context.Request.Query["traffic"].ToString(),
                context.Request.Query["exact"].ToString(),
                engineOptions.MaxQueryLimit,
                engineOptions.DefaultSessionQueryRangeHours,
                DateTime.UtcNow,
                identity.TenantSet);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == TopEventsRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            TopEventsResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);

            return Results.Ok(response);
        })
        .WithName("GetTopEvents")
        .Produces<TopEventsResponse>(StatusCodes.Status200OK)
        .RequireAuthorization()
        .RequireReadScope("top");
    }
}

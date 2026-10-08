namespace Nealytics.Engine.Features.GetBreakdown;

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

public static class GetBreakdownEndpoint
{
    public static void MapGetBreakdown(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/breakdown", async (
            HttpContext context,
            GetBreakdownQuery query,
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

            BreakdownRequestResult parsed = BreakdownRequestFactory.Create(
                identity.ProjectId,
                identity.TenantId,
                context.Request.Query["metric"].ToString(),
                context.Request.Query["groupBy"].ToString(),
                context.Request.Query["eventType"].ToString(),
                context.Request.Query["filter"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                context.Request.Query["from"].ToString(),
                context.Request.Query["to"].ToString(),
                context.Request.Query["limit"].ToString(),
                context.Request.Query["orderBy"].ToString(),
                context.Request.Query["traffic"].ToString(),
                context.Request.Query["exact"].ToString(),
                context.Request.Query["mode"].ToString(),
                columns,
                measures,
                engineOptions.MaxQueryLimit,
                engineOptions.DefaultSessionQueryRangeHours,
                DateTime.UtcNow,
                identity.TenantSet);

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
        .RequireAuthorization()
        .RequireReadScope("breakdown");
    }
}

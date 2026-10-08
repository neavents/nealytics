namespace Nealytics.Engine.Features.GetDistribution;

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

public static class GetDistributionEndpoint
{
    public static void MapGetDistribution(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/distribution", async (
            HttpContext context,
            GetDistributionQuery query,
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

            DistributionRequestResult parsed = DistributionRequestFactory.Create(
                identity.ProjectId,
                identity.TenantId,
                context.Request.Query["of"].ToString(),
                context.Request.Query["eventType"].ToString(),
                context.Request.Query["filter"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                context.Request.Query["from"].ToString(),
                context.Request.Query["to"].ToString(),
                context.Request.Query["quantiles"].ToString(),
                context.Request.Query["buckets"].ToString(),
                context.Request.Query["traffic"].ToString(),
                context.Request.Query["mode"].ToString(),
                columns,
                measures,
                engineOptions.DefaultSessionQueryRangeHours,
                DateTime.UtcNow,
                identity.TenantSet);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == DistributionRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            DistributionResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);
            return Results.Ok(response);
        })
        .WithName("GetDistribution")
        .Produces<DistributionResponse>(StatusCodes.Status200OK)
        .RequireAuthorization()
        .RequireReadScope("distribution");
    }
}

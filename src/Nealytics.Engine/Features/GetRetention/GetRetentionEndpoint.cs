namespace Nealytics.Engine.Features.GetRetention;

using System;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;

public static class GetRetentionEndpoint
{
    public static void MapGetRetention(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/retention", async (
            HttpContext context,
            GetRetentionQuery query,
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

            RetentionRequestResult parsed = RetentionRequestFactory.Create(
                identity.ProjectId,
                identity.TenantId,
                context.Request.Query["period"].ToString(),
                context.Request.Query["by"].ToString(),
                context.Request.Query["eventType"].ToString(),
                context.Request.Query["filter"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                context.Request.Query["from"].ToString(),
                context.Request.Query["to"].ToString(),
                context.Request.Query["traffic"].ToString(),
                columns,
                measures,
                options.Value.AliasEventType,
                DateTime.UtcNow,
                identity.TenantSet);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == RetentionRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            RetentionResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);
            return Results.Ok(response);
        })
        .WithName("GetRetention")
        .Produces<RetentionResponse>(StatusCodes.Status200OK)
        .RequireAuthorization()
        .RequireReadScope("retention");
    }
}

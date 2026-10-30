namespace Nealytics.Engine.Features.GetNamedMetric;

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

public static class GetNamedMetricEndpoint
{
    public static void MapGetNamedMetric(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/metrics/{name}", async (
            string name,
            HttpContext context,
            NamedMetricRegistry metrics,
            EventSegmentRegistry segments,
            QueryColumns columns,
            MeasureRegistry measures,
            TenantAttributeRegistry tenantAttributes,
            IOptions<TelemetryEngineOptions> options,
            GetNamedMetricQuery query,
            CancellationToken cancellationToken) =>
        {
            NamedMetricRequestResult parsed = NamedMetricRequestFactory.Create(
                ReadClaims.Resolve(context, tenantAttributes), name,
                context.Request.Query["from"].ToString(), context.Request.Query["to"].ToString(),
                context.Request.Query["traffic"].ToString(),
                context.Request.Query["filter"].Select(value => value!).ToArray(),
                metrics, columns, measures, options.Value.DefaultSessionQueryRangeHours, DateTime.UtcNow,
                segments, context.Request.Query["segment"].ToString());
            if (parsed.Request is null)
            {
                return parsed.StatusCode == 403 ? Results.Forbid() : Results.Json(parsed.Error, statusCode: parsed.StatusCode);
            }
            return Results.Ok(await query.ExecuteAsync(parsed.Request, cancellationToken));
        })
        .WithName("GetNamedMetric")
        .Produces<NamedMetricResponse>(StatusCodes.Status200OK)
        .RequireAuthorization()
        .RequireReadScope("metrics");
    }
}

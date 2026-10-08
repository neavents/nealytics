namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;
using System.Threading;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;

public static class GetEventTimeSeriesEndpoint
{
    public static void MapGetEventTimeSeries(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/timeseries", async (
            HttpContext context,
            GetEventTimeSeriesQuery query,
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

            EventTimeSeriesRequestResult parsed = EventTimeSeriesRequestFactory.Create(
                identity.ProjectId,
                identity.TenantId,
                context.Request.Query["limit"].ToString(),
                context.Request.Query["interval"].ToString(),
                context.Request.Query["from"].ToString(),
                context.Request.Query["to"].ToString(),
                context.Request.Query["eventType"].ToString(),
                context.Request.Query["groupBy"].ToString(),
                context.Request.Query["tz"].ToString(),
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
                return parsed.ErrorStatusCode == EventTimeSeriesRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            try
            {
                EventTimeSeriesResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);
                return Results.Ok(response);
            }
            catch (Exception exception) when (ClickHouseArgumentFault.IsUnknownTimeZone(exception))
            {
                return Results.BadRequest(
                    "'tz' is not a time zone ClickHouse knows. Use an IANA zone name such as "
                    + "Europe/Istanbul.");
            }
        })
        .WithName("GetEventTimeSeries")
        .Produces<EventTimeSeriesResponse>(StatusCodes.Status200OK)
        .RequireAuthorization()
        .RequireReadScope("timeseries");
    }
}

namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;
using System.Security.Claims;
using System.Threading;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Configuration;
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
            QueryGuard guard,
            CancellationToken cancellationToken) =>
        {
            ClaimsPrincipal user = context.User;
            TelemetryEngineOptions engineOptions = options.Value;

            EventTimeSeriesRequestResult parsed = EventTimeSeriesRequestFactory.Create(
                user.FindFirst("project_id")?.Value,
                user.FindFirst("tenant_id")?.Value,
                context.Request.Query["limit"].ToString(),
                context.Request.Query["interval"].ToString(),
                context.Request.Query["from"].ToString(),
                context.Request.Query["to"].ToString(),
                context.Request.Query["eventType"].ToString(),
                context.Request.Query["groupBy"].ToString(),
                context.Request.Query["tz"].ToString(),
                context.Request.Query["traffic"].ToString(),
                // Repeatable, like /breakdown's. ToArray, not ToString: a second filter must add a
                // condition, not overwrite the first and silently widen the result.
                context.Request.Query["filter"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                columns,
                measures,
                engineOptions.MaxQueryLimit,
                engineOptions.DefaultSessionQueryRangeHours,
                DateTime.UtcNow);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == EventTimeSeriesRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            if (!guard.Admits(parsed.Request.From, parsed.Request.To))
            {
                return guard.RejectRange(parsed.Request.From, parsed.Request.To);
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
        .RequireAuthorization();
    }
}

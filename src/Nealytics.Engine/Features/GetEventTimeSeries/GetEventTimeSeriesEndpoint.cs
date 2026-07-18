namespace Nealytics.Engine.Features.GetEventTimeSeries;

using System;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;

public static class GetEventTimeSeriesEndpoint
{
    public static void MapGetEventTimeSeries(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/timeseries", async (
            HttpContext context,
            GetEventTimeSeriesQuery query,
            IOptions<TelemetryEngineOptions> options,
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
                engineOptions.MaxQueryLimit,
                engineOptions.DefaultSessionQueryRangeHours,
                DateTime.UtcNow);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == EventTimeSeriesRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            EventTimeSeriesResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);

            return Results.Ok(response);
        })
        .WithName("GetEventTimeSeries")
        .Produces<EventTimeSeriesResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

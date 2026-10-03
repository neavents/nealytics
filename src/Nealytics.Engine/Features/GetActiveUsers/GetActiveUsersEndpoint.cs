namespace Nealytics.Engine.Features.GetActiveUsers;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;

public static class GetActiveUsersEndpoint
{
    public static void MapGetActiveUsers(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/active", async (
            HttpContext context,
            GetActiveUsersQuery query,
            QueryColumns columns,
            MeasureRegistry measures,
            IOptions<TelemetryEngineOptions> options,
            CancellationToken cancellationToken) =>
        {
            ClaimsPrincipal user = context.User;
            TelemetryEngineOptions engineOptions = options.Value;

            ActiveUsersRequestResult parsed = ActiveUsersRequestFactory.Create(
                user.FindFirst("project_id")?.Value,
                user.FindFirst("tenant_id")?.Value,
                limitRaw: context.Request.Query["limit"].ToString(),
                intervalRaw: context.Request.Query["interval"].ToString(),
                byRaw: context.Request.Query["by"].ToString(),
                modeRaw: context.Request.Query["mode"].ToString(),
                fromRaw: context.Request.Query["from"].ToString(),
                toRaw: context.Request.Query["to"].ToString(),
                tzRaw: context.Request.Query["tz"].ToString(),
                trafficRaw: context.Request.Query["traffic"].ToString(),
                eventType: context.Request.Query["eventType"].ToString(),
                filtersRaw: context.Request.Query["filter"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                columns: columns,
                measures: measures,
                maxLimit: engineOptions.MaxQueryLimit,
                defaultRangeHours: engineOptions.DefaultSessionQueryRangeHours,
                nowUtc: DateTime.UtcNow);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == ActiveUsersRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            try
            {
                ActiveUsersResponse response = await query.ExecuteAsync(parsed.Request, cancellationToken);
                return Results.Ok(response);
            }
            catch (Exception exception) when (ClickHouseArgumentFault.IsUnknownTimeZone(exception))
            {
                return Results.BadRequest(
                    "'tz' is not a time zone ClickHouse knows. Use an IANA zone name such as "
                    + "Europe/Istanbul.");
            }
        })
        .WithName("GetActiveUsers")
        .Produces<ActiveUsersResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

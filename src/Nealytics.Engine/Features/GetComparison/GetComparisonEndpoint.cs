namespace Nealytics.Engine.Features.GetComparison;

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

public static class GetComparisonEndpoint
{
    public static void MapGetComparison(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/compare", async (
            HttpContext context,
            GetComparisonQuery query,
            QueryColumns columns,
            MeasureRegistry measures,
            IOptions<TelemetryEngineOptions> options,
            QueryGuard guard,
            CancellationToken cancellationToken) =>
        {
            ClaimsPrincipal user = context.User;
            IQueryCollection parameters = context.Request.Query;

            ComparisonRequestResult parsed = ComparisonRequestFactory.Create(
                new ComparisonQueryParameters
                {
                    ProjectId = user.FindFirst("project_id")?.Value,
                    TenantId = user.FindFirst("tenant_id")?.Value,
                    Metrics = parameters["metric"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                    GroupBy = parameters["groupBy"].ToString(),
                    Filters = parameters["filter"].ToArray().Where(v => v is not null).Select(v => v!).ToArray(),
                    From = parameters["from"].ToString(),
                    To = parameters["to"].ToString(),
                    PreviousFrom = parameters["previousFrom"].ToString(),
                    PreviousTo = parameters["previousTo"].ToString(),
                    TimeZone = parameters["tz"].ToString(),
                    Traffic = parameters["traffic"].ToString(),
                    Mode = parameters["mode"].ToString(),
                    Exact = parameters["exact"].ToString(),
                    Limit = parameters["limit"].ToString(),
                    OrderBy = parameters["orderBy"].ToString(),
                    Order = parameters["order"].ToString(),
                    Empty = parameters["empty"].ToString(),
                },
                columns,
                measures,
                options.Value.MaxQueryLimit,
                options.Value.DefaultSessionQueryRangeHours,
                DateTime.UtcNow);

            if (!parsed.Success)
            {
                return parsed.ErrorStatusCode == ComparisonRequestFactory.StatusForbidden
                    ? Results.Forbid()
                    : Results.BadRequest(parsed.ErrorMessage);
            }

            ComparisonRequest request = parsed.Request;

            if (!guard.Admits(request.From, request.To))
            {
                return guard.RejectRange(request.From, request.To);
            }

            if (!guard.Admits(request.PreviousFrom, request.PreviousTo))
            {
                return guard.RejectRange(request.PreviousFrom, request.PreviousTo);
            }

            ComparisonResponse response = await query.ExecuteAsync(request, cancellationToken);
            return Results.Ok(response);
        })
        .WithName("GetComparison")
        .Produces<ComparisonResponse>(StatusCodes.Status200OK)
        .RequireAuthorization();
    }
}

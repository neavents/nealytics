namespace Nealytics.Engine.Features.GetSavedReport;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;

public static class GetSavedReportEndpoint
{
    public static void MapGetSavedReport(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/reports/{name}", async (
            string name,
            HttpContext context,
            SavedReportRegistry reports,
            NamedMetricRegistry metrics,
            EventSegmentRegistry segments,
            AnalyticsColumns columns,
            MeasureRegistry measures,
            TenantAttributeRegistry tenantAttributes,
            IOptions<TelemetryEngineOptions> options,
            GetSavedReportQuery query,
            CancellationToken cancellationToken) =>
        {
            SavedReportRequestResult parsed = SavedReportRequestFactory.Create(
                ReadClaims.Resolve(context, tenantAttributes), name,
                context.Request.Query["from"].ToString(), context.Request.Query["to"].ToString(),
                context.Request.Query["traffic"].ToString(),
                context.Request.Query["filter"].Select(value => value!).ToArray(),
                reports, metrics, segments, columns.Value, measures, options.Value.DefaultSessionQueryRangeHours, DateTime.UtcNow,
                context.Request.Query.Where(pair => pair.Key.StartsWith("param.", StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key[6..], pair => pair.Value.Select(value => value!).ToArray(), StringComparer.Ordinal),
                context.Request.Query["execution"].ToString(), context.Request.Query["accuracy"].ToString());
            if (parsed.Request is null)
            {
                return parsed.StatusCode == 403 ? Results.Forbid() : Results.Json(parsed.Error, statusCode: parsed.StatusCode);
            }
            string? unsupported = SavedReportRequestFactory.UnsupportedParameter(context.Request.Query.Keys, parsed.Request.Definition.Parameters);
            if (unsupported is not null)
            {
                return Results.BadRequest(unsupported);
            }
            return Results.Ok(await query.ExecuteAsync(parsed.Request, cancellationToken));
        })
        .WithName("GetSavedReport")
        .Produces<SavedReportResponse>(StatusCodes.Status200OK)
        .RequireAuthorization()
        .RequireReadScope("reports");
    }
}

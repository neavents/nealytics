namespace Nealytics.Engine.Features.GetAnalyticsPlan;

using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.GetNamedMetric;
using Nealytics.Engine.Features.GetSavedReport;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;

public static class GetAnalyticsPlanEndpoint
{
    public static void MapGetAnalyticsPlan(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/analytics/metrics/{name}/explain", (
            string name, HttpContext context, ProjectRequestContext projectContext,
            AnalyticsPlanner planner, IOptions<TelemetryEngineOptions> options) =>
        {
            ProjectContext project = projectContext.Current;
            NamedMetricRequestResult parsed = NamedMetricRequestFactory.Create(
                ReadClaims.Resolve(context, project.TenantAttributes), name,
                context.Request.Query["from"].ToString(), context.Request.Query["to"].ToString(),
                context.Request.Query["traffic"].ToString(), context.Request.Query["filter"].Select(value => value!).ToArray(),
                project.NamedMetrics, project.Analytics.Value, project.Measures, options.Value.DefaultSessionQueryRangeHours,
                DateTime.UtcNow, project.EventSegments, context.Request.Query["segment"].ToString(),
                execution: context.Request.Query["execution"].ToString(), accuracy: context.Request.Query["accuracy"].ToString());
            if (parsed.Request is null)
            {
                return parsed.StatusCode == 403 ? Results.Forbid() : Results.Json(parsed.Error, statusCode: parsed.StatusCode);
            }
            NamedMetricRequest request = parsed.Request;
            MetricProjection projection = new([request.Definition]);
            AnalyticsPlan plan = planner.Select(projection.Components, request.Scope, null, request.Execution, explain: true);
            return Results.Ok(Response(request.Definition.Name, request.Definition.Version, request.Definition.Accuracy, plan));
        })
        .WithName("ExplainNamedMetric")
        .Produces<AnalyticsPlanResponse>(StatusCodes.Status200OK)
        .RequireAuthorization(policy => policy.RequireClaim("role", "admin"))
        .RequireReadScope("plans")
        .RequireReadScope("metrics");

        endpoints.MapGet("/api/v1/analytics/reports/{name}/explain", (
            string name, HttpContext context, ProjectRequestContext projectContext,
            AnalyticsPlanner planner, IOptions<TelemetryEngineOptions> options) =>
        {
            ProjectContext project = projectContext.Current;
            SavedReportRequestResult parsed = SavedReportRequestFactory.Create(
                ReadClaims.Resolve(context, project.TenantAttributes), name,
                context.Request.Query["from"].ToString(), context.Request.Query["to"].ToString(),
                context.Request.Query["traffic"].ToString(), context.Request.Query["filter"].Select(value => value!).ToArray(),
                project.SavedReports, project.NamedMetrics, project.EventSegments, project.Analytics.Value, project.Measures,
                options.Value.DefaultSessionQueryRangeHours, DateTime.UtcNow,
                context.Request.Query.Where(pair => pair.Key.StartsWith("param.", StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key[6..], pair => pair.Value.Select(value => value!).ToArray(), StringComparer.Ordinal),
                context.Request.Query["execution"].ToString(), context.Request.Query["accuracy"].ToString());
            if (parsed.Request is null)
            {
                return parsed.StatusCode == 403 ? Results.Forbid() : Results.Json(parsed.Error, statusCode: parsed.StatusCode);
            }
            SavedReportRequest request = parsed.Request;
            string? unsupported = SavedReportRequestFactory.UnsupportedParameter(context.Request.Query.Keys, request.Definition.Parameters);
            if (unsupported is not null)
            {
                return Results.BadRequest(unsupported);
            }
            MetricProjection projection = new(request.Definition.Fields.Select(field => field.Definition));
            AnalyticsPlan plan = planner.Select(projection.Components, request.Scope, request.Definition, request.Execution, explain: true);
            return Results.Ok(Response(request.Definition.Name, request.Definition.Version,
                AnalyticsExecution.Accuracy(request.Definition.Fields.Select(field => field.Definition)), plan));
        })
        .WithName("ExplainSavedReport")
        .Produces<AnalyticsPlanResponse>(StatusCodes.Status200OK)
        .RequireAuthorization(policy => policy.RequireClaim("role", "admin"))
        .RequireReadScope("plans")
        .RequireReadScope("reports");
    }

    private static AnalyticsPlanResponse Response(string name, int version, string accuracy, AnalyticsPlan plan) => new()
    {
        Name = name,
        Version = version,
        Accuracy = accuracy,
        Source = plan.Source,
        SelectedRollup = plan.Rollup?.Name,
        Slices = plan.Slices,
        Candidates = plan.Candidates
    };
}

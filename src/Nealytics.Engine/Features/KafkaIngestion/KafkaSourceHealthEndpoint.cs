namespace Nealytics.Engine.Features.KafkaIngestion;

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Security;

public static class KafkaSourceHealthEndpoint
{
    public static void MapKafkaSourceHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/telemetry/sources/kafka/default", Get)
            .WithName("GetKafkaSourceHealth").Produces<KafkaSourceHealthResponse>();
    }

    private static IResult Get(HttpContext context, ApiKeyValidator keys, KafkaConnectorSettings settings,
        KafkaSourceHealth health)
    {
        context.Response.Headers.CacheControl = "no-store";
        string key = IngestValidation.ResolveProjectKey(context.Request.Headers["X-Project-Key"].ToString(),
            context.Request.Query["k"].ToString());
        if (!keys.TryResolve(key, out IngestionKeyPolicy? policy))
        {
            return Results.Unauthorized();
        }
        if (!policy.IsServer || string.IsNullOrEmpty(policy.PinnedProject)
            || !ApiKeyValidator.MayWriteProject(policy, policy.PinnedProject))
        {
            return Results.Forbid();
        }
        return string.Equals(policy.PinnedProject, settings.Policy.PinnedProject, StringComparison.Ordinal)
            ? Results.Json(health.Snapshot()) : Results.NotFound();
    }
}

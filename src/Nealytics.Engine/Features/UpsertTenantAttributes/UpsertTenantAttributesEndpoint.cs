namespace Nealytics.Engine.Features.UpsertTenantAttributes;

using System;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;

public static class UpsertTenantAttributesEndpoint
{
    public static void MapUpsertTenantAttributes(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/tenants/attributes", async (
            HttpContext context,
            ApiKeyValidator keyValidator,
            TenantAttributeRegistry registry,
            ITenantAttributeWriter writer,
            IOptions<TelemetryEngineOptions> options,
            CancellationToken cancellationToken) =>
        {
            string key = IngestValidation.ResolveProjectKey(
                context.Request.Headers["X-Project-Key"].ToString(),
                context.Request.Query["k"].ToString());

            if (key.Length == 0 || !keyValidator.TryResolve(key, out IngestionKeyPolicy? policy))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            if (!policy.IsServer)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            TenantAttributesPayload? payload;

            try
            {
                payload = await JsonSerializer.DeserializeAsync(
                    context.Request.Body, TelemetryAotContext.Default.TenantAttributesPayload, cancellationToken);
            }
            catch (JsonException exception)
            {
                return Results.BadRequest(exception.Message);
            }

            TenantAttributesRequestResult parsed = TenantAttributesRequestFactory.Create(
                payload, registry, options.Value.MaxTenantAttributeBatch);

            if (!parsed.Success)
            {
                return Results.BadRequest(parsed.ErrorMessage);
            }

            if (!ApiKeyValidator.MayWriteProject(policy, parsed.ProjectId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await writer.WriteAsync(parsed.ProjectId, parsed.Rows, DateTime.UtcNow, cancellationToken);

            return Results.Ok(new TenantAttributesResponse { Tenants = parsed.TenantCount, Values = parsed.Rows.Count });
        })
        .WithName("UpsertTenantAttributes")
        .Produces<TenantAttributesResponse>(StatusCodes.Status200OK)
        .RequireRateLimiting("ingestion");
    }
}

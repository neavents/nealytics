namespace Nealytics.Engine.Features.ImportNdjson;

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Storage;

public static class ImportNdjsonEndpoint
{
    public static void MapNdjsonImport(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/telemetry/import/ndjson", HandleAsync)
            .WithName("ImportNdjson")
            .WithMetadata(new ClickHouseWorkloadMetadata(ClickHouseWorkload.Write))
            .Produces<NdjsonImportResponse>(StatusCodes.Status202Accepted)
            .RequireRateLimiting("ndjson-import");
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context, NdjsonImporter importer, IngestionRequestReader reader,
        ApiKeyValidator keys, IOptions<TelemetryEngineOptions> options)
    {
        string key = IngestValidation.ResolveProjectKey(
            context.Request.Headers["X-Project-Key"].ToString(), context.Request.Query["k"].ToString());
        if (!keys.TryResolve(key, out IngestionKeyPolicy? policy))
        {
            return Results.Unauthorized();
        }
        if (!policy.IsServer || string.IsNullOrEmpty(policy.PinnedProject))
        {
            return Results.Forbid();
        }
        if (!AcceptsContentType(context.Request.ContentType))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }
        int limit = options.Value.MaxRequestBodyBytes;
        if (IngestValidation.ExceedsBodyLimit(context.Request.ContentLength, limit))
        {
            NdjsonImportResponse oversized = new(0, 0, "body_too_large");
            return Results.Json(oversized, statusCode: oversized.StatusCode);
        }
        IngestValidation.ApplyBodyLimit(context, limit);
        NdjsonImportResponse result = await importer.IngestAsync(
            reader.Open(context), policy, DateTime.UtcNow, context.RequestAborted);
        return Results.Json(result, statusCode: result.StatusCode);
    }

    internal static bool AcceptsContentType(string? value) =>
        MediaTypeHeaderValue.TryParse(value, out MediaTypeHeaderValue? parsed)
        && string.Equals(parsed.MediaType.Value, "application/x-ndjson", StringComparison.OrdinalIgnoreCase)
        && (!parsed.Charset.HasValue || string.Equals(
            HeaderUtilities.RemoveQuotes(parsed.Charset).Value, "utf-8", StringComparison.OrdinalIgnoreCase));
}

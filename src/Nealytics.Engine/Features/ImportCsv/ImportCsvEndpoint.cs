namespace Nealytics.Engine.Features.ImportCsv;

using System;
using System.Linq;
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

public static class ImportCsvEndpoint
{
    public static void MapCsvImport(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/telemetry/import/csv", HandleAsync)
            .WithName("ImportCsv")
            .WithMetadata(new ClickHouseWorkloadMetadata(ClickHouseWorkload.Write))
            .Produces<CsvImportResponse>(StatusCodes.Status202Accepted)
            .RequireRateLimiting("ndjson-import");
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context, CsvImporter importer, CsvSourceMappings mappings, IngestionRequestReader reader,
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
        CsvProjectMapping? mapping = mappings.Find(policy.PinnedProject);
        if (mapping is null)
        {
            return Results.NotFound();
        }
        if (!AcceptsContentType(context.Request.ContentType))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }
        int limit = options.Value.MaxRequestBodyBytes;
        if (IngestValidation.ExceedsBodyLimit(context.Request.ContentLength, limit))
        {
            CsvImportResponse oversized = new(0, 0, "body_too_large");
            return Results.Json(oversized, statusCode: oversized.StatusCode);
        }
        IngestValidation.ApplyBodyLimit(context, limit);
        CsvImportResponse result = await importer.IngestAsync(
            reader.Open(context), mapping, policy, DateTime.UtcNow, context.RequestAborted);
        return Results.Json(result, statusCode: result.StatusCode);
    }

    internal static bool AcceptsContentType(string? value) =>
        MediaTypeHeaderValue.TryParse(value, out MediaTypeHeaderValue? parsed)
        && string.Equals(parsed.MediaType.Value, "text/csv", StringComparison.OrdinalIgnoreCase)
        && (!parsed.Charset.HasValue || string.Equals(
            HeaderUtilities.RemoveQuotes(parsed.Charset).Value, "utf-8", StringComparison.OrdinalIgnoreCase))
        && !parsed.Parameters.Any(parameter => parameter.Name.Equals("header", StringComparison.OrdinalIgnoreCase)
            && !HeaderUtilities.RemoveQuotes(parameter.Value).Equals("present", StringComparison.OrdinalIgnoreCase));
}

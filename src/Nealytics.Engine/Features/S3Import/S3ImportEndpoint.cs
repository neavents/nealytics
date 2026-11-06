namespace Nealytics.Engine.Features.S3Import;

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;

public static class S3ImportEndpoint
{
    public static void MapS3Import(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/telemetry/import/s3/{source}/inspect", InspectAsync)
            .WithName("InspectS3Object")
            .WithMetadata(new ClickHouseWorkloadMetadata(ClickHouseWorkload.Write))
            .Produces<S3ObjectSnapshot>()
            .RequireRateLimiting("ndjson-import");
        endpoints.MapPost("/api/v1/telemetry/import/s3/{source}/jobs/{jobId:guid}/chunks/{chunkId:guid}", AppendAsync)
            .WithName("ImportS3Chunk")
            .WithMetadata(new ClickHouseWorkloadMetadata(ClickHouseWorkload.Write))
            .Produces<ImportJobResponse>(202)
            .RequireRateLimiting("ndjson-import");
    }

    private static async Task<IResult> InspectAsync(string source, HttpContext context, S3Sources sources,
        ApiKeyValidator keys, S3ImportCoordinator coordinator, IOptions<TelemetryEngineOptions> options)
    {
        IResult? rejected = Authenticate(source, context, keys, sources, out _, out S3Source? selected);
        if (rejected is not null)
        {
            return rejected;
        }
        try
        {
            S3ObjectRequest? request = await ObjectImportControl.ReadAsync(context, options.Value.MaxRequestBodyBytes,
                TelemetryAotContext.Default.S3ObjectRequest);
            if (request is null)
            {
                return Error(400, "invalid_object_request");
            }
            S3ObjectSnapshot snapshot = await coordinator.InspectAsync(selected!, request, context.RequestAborted);
            return Results.Json(snapshot);
        }
        catch (S3ImportException exception)
        {
            return Error(exception.StatusCode, exception.Error);
        }
        catch (ObjectControlException exception)
        {
            return Error(exception.StatusCode, exception.Error);
        }
        catch (JsonException)
        {
            return Error(400, "invalid_object_request");
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            return Error(413, "control_body_too_large");
        }
    }

    private static async Task<IResult> AppendAsync(string source, Guid jobId, Guid chunkId, HttpContext context,
        S3Sources sources, ApiKeyValidator keys, S3ImportCoordinator coordinator, NdjsonImporter importer,
        IOptions<TelemetryEngineOptions> options)
    {
        IResult? rejected = Authenticate(source, context, keys, sources, out IngestionKeyPolicy? policy, out S3Source? selected);
        if (rejected is not null)
        {
            return rejected;
        }
        if (!long.TryParse(context.Request.Query["offset"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long offset))
        {
            return Error(400, "invalid_object_request");
        }
        try
        {
            S3ObjectSnapshot? request = await ObjectImportControl.ReadAsync(context, options.Value.MaxRequestBodyBytes,
                TelemetryAotContext.Default.S3ObjectSnapshot);
            if (request is null)
            {
                return Error(400, "invalid_object_request");
            }
            DateTime receivedAt = DateTime.UtcNow;
            ImportJobResult result = await coordinator.AppendAsync(selected!, request, jobId, chunkId, offset,
                reader => importer.IngestAsync(reader, policy!, receivedAt, context.RequestAborted), context.RequestAborted);
            return result.Job is null ? Error(result.StatusCode, result.Error!)
                : Results.Json(result.Job, statusCode: result.StatusCode);
        }
        catch (S3ImportException exception)
        {
            return Error(exception.StatusCode, exception.Error);
        }
        catch (ObjectControlException exception)
        {
            return Error(exception.StatusCode, exception.Error);
        }
        catch (JsonException)
        {
            return Error(400, "invalid_object_request");
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            return Error(413, "control_body_too_large");
        }
        catch (IOException)
        {
            return Error(503, "job_storage_unavailable");
        }
    }

    private static IResult? Authenticate(string name, HttpContext context, ApiKeyValidator keys, S3Sources sources,
        out IngestionKeyPolicy? policy, out S3Source? source)
    {
        source = null;
        IResult? rejected = ObjectImportControl.Authenticate(context, keys, out policy);
        if (rejected is not null)
        {
            return rejected;
        }
        source = sources.Find(name, policy!.PinnedProject!);
        return source is null ? Error(404, "source_not_found") : null;
    }

    private static IResult Error(int status, string error) => Results.Json(new ImportJobError(error), statusCode: status);
}

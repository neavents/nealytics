namespace Nealytics.Engine.Features.ImportJobs;

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Storage;

public static class ImportJobsEndpoint
{
    public static void MapImportJobs(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/telemetry/import/jobs", List)
            .WithName("ListImportJobs").Produces<ImportJobPage>();
        endpoints.MapGet("/api/v1/telemetry/import/jobs/{jobId:guid}", Get)
            .WithName("GetImportJob").Produces<ImportJobResponse>();
        endpoints.MapPost("/api/v1/telemetry/import/jobs/{jobId:guid}/chunks/{chunkId:guid}", AppendAsync)
            .WithName("AppendImportChunk")
            .WithMetadata(new ClickHouseWorkloadMetadata(ClickHouseWorkload.Write))
            .Produces<ImportJobResponse>(StatusCodes.Status202Accepted)
            .RequireRateLimiting("ndjson-import");
    }

    private static IResult List(HttpContext context, ApiKeyValidator keys, ImportJobStore jobs)
    {
        IResult? rejected = Authenticate(context, keys, out IngestionKeyPolicy? policy);
        if (rejected is not null)
        {
            return rejected;
        }
        ImportJobListRequest? request = ImportJobListRequest.Parse(context.Request.Query);
        if (request is null)
        {
            return Results.Json(new ImportJobError("invalid_job_list_request"), statusCode: 400);
        }
        try
        {
            return Results.Json(jobs.List(policy!.PinnedProject!, request.Limit, request.After));
        }
        catch (IOException)
        {
            return Results.Json(new ImportJobError("job_storage_unavailable"), statusCode: 503);
        }
    }

    private static IResult Get(Guid jobId, HttpContext context, ApiKeyValidator keys, ImportJobCoordinator jobs)
    {
        IResult? rejected = Authenticate(context, keys, out IngestionKeyPolicy? policy);
        if (rejected is not null)
        {
            return rejected;
        }
        try
        {
            return Result(jobs.Get(jobId, policy!.PinnedProject!));
        }
        catch (IOException)
        {
            return Result(ImportJobResult.Reject(503, "job_storage_unavailable"));
        }
    }

    private static async Task<IResult> AppendAsync(Guid jobId, Guid chunkId, HttpContext context,
        ApiKeyValidator keys, ImportJobCoordinator jobs, NdjsonImporter importer, IngestionRequestReader reader,
        ImportJobLimits limits)
    {
        IResult? rejected = Authenticate(context, keys, out IngestionKeyPolicy? policy);
        if (rejected is not null)
        {
            return rejected;
        }
        if (!ImportNdjsonEndpoint.AcceptsContentType(context.Request.ContentType))
        {
            return Result(ImportJobResult.Reject(415, "unsupported_content_type"));
        }
        ImportJobRequest? request = ImportJobRequest.Parse(jobId, chunkId, context.Request);
        if (request is null)
        {
            return Result(ImportJobResult.Reject(400, "invalid_job_request"));
        }
        if (IngestValidation.ExceedsBodyLimit(context.Request.ContentLength, limits.ChunkBytes))
        {
            return Result(ImportJobResult.Reject(413, "chunk_too_large"));
        }
        IngestValidation.ApplyBodyLimit(context, limits.ChunkBytes);
        try
        {
            DateTime receivedAt = DateTime.UtcNow;
            ImportJobResult result = await jobs.AppendAsync(request, policy!.PinnedProject!, reader.Open(context),
                source => importer.IngestAsync(source, policy, receivedAt, context.RequestAborted), context.RequestAborted);
            return Result(result);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            return Result(ImportJobResult.Reject(413, "chunk_too_large"));
        }
        catch (IOException)
        {
            return Result(ImportJobResult.Reject(503, "job_storage_unavailable"));
        }
    }

    private static IResult? Authenticate(HttpContext context, ApiKeyValidator keys, out IngestionKeyPolicy? policy)
    {
        string key = IngestValidation.ResolveProjectKey(context.Request.Headers["X-Project-Key"].ToString(),
            context.Request.Query["k"].ToString());
        if (!keys.TryResolve(key, out policy))
        {
            return Results.Unauthorized();
        }
        return !policy.IsServer || string.IsNullOrEmpty(policy.PinnedProject)
            || !ApiKeyValidator.MayWriteProject(policy, policy.PinnedProject) ? Results.Forbid() : null;
    }

    private static IResult Result(ImportJobResult result) => result.Job is null
        ? Results.Json(new ImportJobError(result.Error!), statusCode: result.StatusCode)
        : Results.Json(result.Job, statusCode: result.StatusCode);
}

namespace Nealytics.Engine.Features.ImportJobs;

using System;
using System.IO;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Security;

internal sealed class ObjectControlException(int statusCode, string error) : Exception(error)
{
    public int StatusCode { get; } = statusCode;
    public string Error { get; } = error;
}

internal static class ObjectImportControl
{
    public static IResult? Authenticate(HttpContext context, ApiKeyValidator keys, out IngestionKeyPolicy? policy)
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

    public static async Task<T?> ReadAsync<T>(HttpContext context, int requestLimit, JsonTypeInfo<T> type)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out MediaTypeHeaderValue? media)
            || !string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || (media.CharSet is not null && !media.CharSet.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ObjectControlException(415, "unsupported_content_type");
        }
        int limit = Math.Min(requestLimit, 16_384);
        if (IngestValidation.ExceedsBodyLimit(context.Request.ContentLength, limit))
        {
            throw new ObjectControlException(413, "control_body_too_large");
        }
        IngestValidation.ApplyBodyLimit(context, limit);
        using LengthLimitedStream body = new(context.Request.Body, limit);
        try
        {
            return await JsonSerializer.DeserializeAsync(body, type, context.RequestAborted);
        }
        catch (IOException exception) when (exception is not BadHttpRequestException)
        {
            throw new ObjectControlException(400, "control_body_unavailable");
        }
    }
}

namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;

public static class IngestTelemetryEndpoint
{
    public static void MapTelemetryIngestion(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/telemetry/track", HandleAsync)
            .RequireRateLimiting("ingestion");
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        DurableIngestDispatcher dispatcher,
        IngestionRequestReader reader,
        ApiKeyValidator keyValidator,
        DimensionSanitizer dimensionSanitizer,
        MeasureSanitizer measureSanitizer,
        IngestGate gate,
        IOptions<TelemetryEngineOptions> options)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("IngestHttpRequest");

        string clientProjectKey = IngestValidation.ResolveProjectKey(
            context.Request.Headers["X-Project-Key"].ToString(),
            context.Request.Query["k"].ToString());

        IngestionKeyPolicy? policy = null;
        if (clientProjectKey.Length == 0 || !keyValidator.TryResolve(clientProjectKey, out policy))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        if (IngestValidation.ExceedsBodyLimit(context.Request.ContentLength, options.Value.MaxRequestBodyBytes))
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        IngestValidation.ApplyBodyLimit(context, options.Value.MaxRequestBodyBytes);

        try
        {
            GlobalTelemetryPayload? payload = await TrackRequestReader.ReadAsync(
                reader.Open(context), context.RequestAborted);

            IngestRejection rejection = gate.Check(policy, payload, DateTime.UtcNow, out bool timestampAdjusted);

            if (rejection != IngestRejection.None)
            {
                TelemetryDiagnostics.EventsRejected.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", IngestValidation.Tag(rejection)),
                    new KeyValuePair<string, object?>("transport", "track"));

                context.Response.Headers["X-Nealytics-Rejected"] = IngestValidation.Tag(rejection);
                return Results.BadRequest();
            }

            dimensionSanitizer.Sanitize(payload!, out IReadOnlyList<string> droppedDimensions);
            measureSanitizer.Sanitize(payload!, out IReadOnlyList<string> droppedMeasures);

            if (droppedDimensions.Count > 0 || droppedMeasures.Count > 0)
            {
                context.Response.Headers["X-Nealytics-Dropped"] =
                    string.Join(",", droppedDimensions.Concat(droppedMeasures));
            }

            if (timestampAdjusted)
            {
                context.Response.Headers["X-Nealytics-Adjusted"] = "timestamp";
            }

            await dispatcher.AppendAsync(payload!, context.RequestAborted);

            return Results.Accepted();
        }
        catch (BadHttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }
    }
}

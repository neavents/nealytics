namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;

public static class IngestTelemetryEndpoint
{
    public static void MapTelemetryIngestion(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/telemetry/track", async (
            HttpContext context,
            TelemetryChannelBroker broker,
            WriteAheadLogger wal,
            ApiKeyValidator keyValidator,
            DimensionSanitizer dimensionSanitizer,
            MeasureSanitizer measureSanitizer,
            IOptions<TelemetryEngineOptions> options) =>
        {
            using Activity? activity = TelemetryDiagnostics.Source.StartActivity("IngestHttpRequest");

            string clientProjectKey = IngestValidation.ResolveProjectKey(
                context.Request.Headers["X-Project-Key"].ToString(),
                context.Request.Query["k"].ToString());

            if (clientProjectKey.Length == 0 || !keyValidator.IsValid(clientProjectKey))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            if (IngestValidation.ExceedsBodyLimit(context.Request.ContentLength, options.Value.MaxRequestBodyBytes))
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            // Covers the chunked case, where there is no Content-Length for the check above to read.
            IngestValidation.ApplyBodyLimit(context, options.Value.MaxRequestBodyBytes);

            try
            {
                // Counted rather than trusted: a chunked request declares no length, so the check
                // above never fires for one.
                PipeReader bodyReader = PipeReader.Create(
                    new LengthLimitedStream(context.Request.Body, options.Value.MaxRequestBodyBytes));
                ReadResult readResult;
                GlobalTelemetryPayload? payload = null;

                while (true)
                {
                    readResult = await bodyReader.ReadAsync(context.RequestAborted);
                    ReadOnlySequence<byte> buffer = readResult.Buffer;

                    if (readResult.IsCompleted)
                    {
                        Utf8JsonReader jsonReader = new Utf8JsonReader(buffer);
                        payload = JsonSerializer.Deserialize(
                            ref jsonReader,
                            TelemetryAotContext.Default.GlobalTelemetryPayload);
                        bodyReader.AdvanceTo(buffer.End);
                        break;
                    }

                    bodyReader.AdvanceTo(buffer.Start, buffer.End);
                }

                IngestRejection rejection = IngestValidation.Validate(payload, DateTime.UtcNow);

                // After the shape checks, because "which project" is only a meaningful question
                // once there is a projectId to ask it about.
                if (rejection == IngestRejection.None
                    && !keyValidator.MayWriteProject(clientProjectKey, payload!.ProjectId))
                {
                    rejection = IngestRejection.ProjectNotPermittedForKey;
                }

                if (rejection != IngestRejection.None)
                {
                    TelemetryDiagnostics.EventsRejected.Add(
                        1,
                        new KeyValuePair<string, object?>("reason", IngestValidation.Tag(rejection)),
                        new KeyValuePair<string, object?>("transport", "track"));

                    // The reason travels in a header rather than a body, so the 400 stays a 400 to
                    // anything parsing status codes while a human running curl still learns which
                    // of six checks refused it.
                    context.Response.Headers["X-Nealytics-Rejected"] = IngestValidation.Tag(rejection);
                    return Results.BadRequest();
                }

                // Before the WAL, so a replay cannot reintroduce a key the registry rejected.
                dimensionSanitizer.Sanitize(payload!, out IReadOnlyList<string> droppedDimensions);
                measureSanitizer.Sanitize(payload!, out IReadOnlyList<string> droppedMeasures);

                // Unlike the beacon, a /track caller is server-side code that can act on this. The
                // event is still accepted with the field removed -- refusing it would turn one
                // misspelled key into total data loss for that event type, which is the failure
                // mode the per-field rule exists to avoid.
                if (droppedDimensions.Count > 0 || droppedMeasures.Count > 0)
                {
                    context.Response.Headers["X-Nealytics-Dropped"] =
                        string.Join(",", droppedDimensions.Concat(droppedMeasures));
                }

                await wal.AppendAsync(payload!, context.RequestAborted);
                await broker.PublishAsync(payload!, context.RequestAborted);

                return Results.Accepted();
            }
            catch (BadHttpRequestException)
            {
                // Raised while reading past the per-request limit set above. A client error, so it
                // must not surface as a 5xx.
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
        })
        .RequireRateLimiting("ingestion");
    }
}

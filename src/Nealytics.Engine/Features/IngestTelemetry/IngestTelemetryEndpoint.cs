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
            IngestGate gate,
            IOptions<TelemetryEngineOptions> options) =>
        {
            using Activity? activity = TelemetryDiagnostics.Source.StartActivity("IngestHttpRequest");

            string clientProjectKey = IngestValidation.ResolveProjectKey(
                context.Request.Headers["X-Project-Key"].ToString(),
                context.Request.Query["k"].ToString());

            if (clientProjectKey.Length == 0 || !keyValidator.TryResolve(clientProjectKey, out IngestionKeyPolicy? policy))
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

                await wal.AppendAsync(payload!, context.RequestAborted);
                await broker.PublishAsync(payload!, context.RequestAborted);

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
        })
        .RequireRateLimiting("ingestion");
    }
}

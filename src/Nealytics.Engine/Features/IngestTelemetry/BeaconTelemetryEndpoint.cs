namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;

public static partial class BeaconTelemetryEndpoint
{
    [LoggerMessage(EventId = 9203, Level = LogLevel.Warning,
        Message = "Refused {RejectedCount} of {TotalCount} element(s) in a beacon batch. The batch "
            + "still returned 204 because sendBeacon cannot retry; see "
            + "nealytics_events_rejected_total for the reasons.")]
    private static partial void LogRejectedBeaconElements(ILogger logger, int rejectedCount, int totalCount);

    public static void MapBeaconIngestion(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/telemetry/beacon", async (
            HttpContext context,
            TelemetryChannelBroker broker,
            WriteAheadLogger wal,
            ApiKeyValidator keyValidator,
            DimensionSanitizer dimensionSanitizer,
            MeasureSanitizer measureSanitizer,
            ILoggerFactory loggerFactory,
            IOptions<TelemetryEngineOptions> options) =>
        {
            using Activity? activity = TelemetryDiagnostics.Source.StartActivity("BeaconIngest");

            string clientProjectKey = IngestValidation.ResolveProjectKey(
                null,
                context.Request.Query["k"].ToString());

            if (clientProjectKey.Length == 0 || !keyValidator.IsValid(clientProjectKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (IngestValidation.ExceedsBodyLimit(context.Request.ContentLength, options.Value.MaxRequestBodyBytes))
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            // Covers the chunked case, where there is no Content-Length for the check above to read.
            IngestValidation.ApplyBodyLimit(context, options.Value.MaxRequestBodyBytes);

            try
            {
                // Counted rather than trusted: a chunked request declares no length, so the check
                // above never fires for one.
                PipeReader bodyReader = PipeReader.Create(
                    new LengthLimitedStream(context.Request.Body, options.Value.MaxRequestBodyBytes));
                int accepted = 0;
                int rejected = 0;
                DateTime receivedAt = DateTime.UtcNow;

                await foreach (GlobalTelemetryPayload? payload in
                    JsonSerializer.DeserializeAsyncEnumerable(
                        bodyReader,
                        TelemetryAotContext.Default.GlobalTelemetryPayload,
                        context.RequestAborted))
                {
                    IngestRejection rejection = IngestValidation.Validate(payload, receivedAt);

                    if (rejection == IngestRejection.None
                        && !keyValidator.MayWriteProject(clientProjectKey, payload!.ProjectId))
                    {
                        rejection = IngestRejection.ProjectNotPermittedForKey;
                    }

                    if (rejection != IngestRejection.None)
                    {
                        // One bad element never fails the batch: sendBeacon cannot retry and its
                        // caller cannot react, so refusing the other 49 events costs far more than
                        // it protects. But it is counted, because the previous bare continue meant
                        // a client could lose every event of one shape and see only 204s.
                        rejected++;
                        TelemetryDiagnostics.EventsRejected.Add(
                            1,
                            new KeyValuePair<string, object?>("reason", IngestValidation.Tag(rejection)),
                            new KeyValuePair<string, object?>("transport", "beacon"));
                        continue;
                    }

                    // Before the WAL, so a replay cannot reintroduce a key the registry rejected.
                    dimensionSanitizer.Sanitize(payload!);
                    measureSanitizer.Sanitize(payload!);

                    await wal.AppendAsync(payload!, context.RequestAborted);
                    await broker.PublishAsync(payload!, context.RequestAborted);
                    accepted++;
                }

                activity?.SetTag("neavents.beacon_events", accepted);
                activity?.SetTag("neavents.beacon_rejected", rejected);

                if (rejected > 0)
                {
                    LogRejectedBeaconElements(
                        loggerFactory.CreateLogger(typeof(BeaconTelemetryEndpoint)),
                        rejected,
                        accepted + rejected);
                }

                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }
            catch (BadHttpRequestException)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            }
            catch (JsonException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
        })
        .RequireRateLimiting("ingestion")
        .RequireCors("beacon");
    }
}

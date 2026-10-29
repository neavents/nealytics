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
            BeaconBatchIngestor ingestor,
            ApiKeyValidator keyValidator,
            ILoggerFactory loggerFactory,
            IOptions<TelemetryEngineOptions> options) =>
        {
            using Activity? activity = TelemetryDiagnostics.Source.StartActivity("BeaconIngest");

            string clientProjectKey = IngestValidation.ResolveProjectKey(
                null,
                context.Request.Query["k"].ToString());

            if (clientProjectKey.Length == 0 || !keyValidator.TryResolve(clientProjectKey, out IngestionKeyPolicy? policy))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (IngestValidation.ExceedsBodyLimit(context.Request.ContentLength, options.Value.MaxRequestBodyBytes))
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            IngestValidation.ApplyBodyLimit(context, options.Value.MaxRequestBodyBytes);

            try
            {
                PipeReader bodyReader = PipeReader.Create(
                    new LengthLimitedStream(context.Request.Body, options.Value.MaxRequestBodyBytes));
                BeaconIngestResult result;
                try
                {
                    IAsyncEnumerable<GlobalTelemetryPayload?> payloads = JsonSerializer.DeserializeAsyncEnumerable(
                        bodyReader,
                        TelemetryAotContext.Default.GlobalTelemetryPayload,
                        context.RequestAborted);
                    result = await ingestor.IngestAsync(payloads, policy!, DateTime.UtcNow, context.RequestAborted);
                }
                finally
                {
                    await bodyReader.CompleteAsync();
                }

                activity?.SetTag("nealytics.beacon_events", result.Accepted);
                activity?.SetTag("nealytics.beacon_rejected", result.Rejected);

                if (result.Rejected > 0)
                {
                    LogRejectedBeaconElements(
                        loggerFactory.CreateLogger(typeof(BeaconTelemetryEndpoint)),
                        result.Rejected,
                        result.Accepted + result.Rejected);
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

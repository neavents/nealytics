namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
        endpoints.MapPost("/api/v1/telemetry/beacon", HandleAsync)
            .RequireRateLimiting("ingestion")
            .RequireCors("beacon");
    }

    private static async Task HandleAsync(
        HttpContext context,
        BeaconBatchIngestor ingestor,
        IngestionRequestReader reader,
        ApiKeyValidator keyValidator,
        ILoggerFactory loggerFactory,
        IOptions<TelemetryEngineOptions> options)
    {
        using Activity? activity = TelemetryDiagnostics.Source.StartActivity("BeaconIngest");

        string clientProjectKey = IngestValidation.ResolveProjectKey(
            null,
            context.Request.Query["k"].ToString());

        IngestionKeyPolicy? policy = null;
        if (clientProjectKey.Length == 0 || !keyValidator.TryResolve(clientProjectKey, out policy))
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
            PipeReader bodyReader = reader.Open(context);
            BeaconIngestResult result = await IngestBodyAsync(bodyReader, ingestor, policy!, context.RequestAborted);

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
    }

    internal static async Task<BeaconIngestResult> IngestBodyAsync(
        PipeReader bodyReader, BeaconBatchIngestor ingestor, IngestionKeyPolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            IAsyncEnumerable<GlobalTelemetryPayload?> payloads = JsonSerializer.DeserializeAsyncEnumerable(
                bodyReader, TelemetryAotContext.Default.GlobalTelemetryPayload, cancellationToken);
            return await ingestor.IngestAsync(payloads, policy, DateTime.UtcNow, cancellationToken);
        }
        finally
        {
            await bodyReader.CompleteAsync();
        }
    }
}

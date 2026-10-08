namespace Nealytics.Engine.Features.ValidateTelemetry;

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;

public static class ValidateTelemetryEndpoint
{
    public static void MapValidateTelemetry(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/telemetry/validate", async (
            HttpContext context,
            ApiKeyValidator keyValidator,
            DimensionSanitizer dimensionSanitizer,
            MeasureSanitizer measureSanitizer,
            DimensionRegistry dimensions,
            MeasureRegistry measures,
            IngestGate gate,
            CancellationToken cancellationToken) =>
        {
            string clientProjectKey = IngestValidation.ResolveProjectKey(
                context.Request.Headers["X-Project-Key"],
                context.Request.Query["k"]);

            if (clientProjectKey.Length == 0 || !keyValidator.TryResolve(clientProjectKey, out IngestionKeyPolicy? policy))
            {
                return Results.Unauthorized();
            }

            GlobalTelemetryPayload? payload;

            try
            {
                payload = await JsonSerializer.DeserializeAsync(
                    context.Request.Body,
                    TelemetryAotContext.Default.GlobalTelemetryPayload,
                    cancellationToken);
            }
            catch (JsonException exception)
            {
                return Results.BadRequest(new ValidateTelemetryResponse
                {
                    Accepted = false,
                    Reason = exception.Message,
                });
            }

            IngestRejection rejection = gate.Check(policy, payload, System.DateTime.UtcNow, out _);

            if (rejection != IngestRejection.None)
            {
                return Results.Ok(new ValidateTelemetryResponse
                {
                    Accepted = false,
                    Reason = Explain(rejection),
                });
            }

            GlobalTelemetryPayload accepted = payload!;

            dimensionSanitizer.Sanitize(accepted, out IReadOnlyList<string> droppedDimensions);
            measureSanitizer.Sanitize(accepted, out IReadOnlyList<string> droppedMeasures);

            TelemetryColumnLayout layout = new(dimensions, measures);
            List<string> rejected = [];

            using (TelemetryColumnBuffers buffers = new(1, layout))
            {
                buffers.Fill([accepted]);

                foreach (DimensionColumnBuffer buffer in buffers.DimensionBuffers)
                {
                    if (buffer.RejectedValueCount > 0)
                    {
                        rejected.Add($"{buffer.Dimension.Name} ({buffer.Dimension.ConfiguredType})");
                    }
                }

                foreach (MeasureColumnBuffer buffer in buffers.MeasureBuffers)
                {
                    if (buffer.RejectedValueCount > 0)
                    {
                        rejected.Add($"{buffer.Measure.Name} ({buffer.Measure.ConfiguredType})");
                    }
                }
            }

            return Results.Ok(new ValidateTelemetryResponse
            {
                Accepted = droppedDimensions.Count == 0
                    && droppedMeasures.Count == 0
                    && rejected.Count == 0,
                DroppedDimensions = droppedDimensions,
                DroppedMeasures = droppedMeasures,
                RejectedValues = rejected,
                Columns = layout.ColumnNames,
                StoredDimensions = accepted.Dimensions,
                StoredMeasures = accepted.Measures,
            });
        })
        .WithName("ValidateTelemetry")
        .Produces<ValidateTelemetryResponse>(StatusCodes.Status200OK)
        .RequireRateLimiting("ingestion");
    }

    private const string Required =
        "projectId, tenantId, sessionId and eventType are all required.";

    private static string Explain(IngestRejection rejection) => rejection switch
    {
        IngestRejection.MissingProjectId => $"projectId is missing. {Required}",
        IngestRejection.MissingTenantId => $"tenantId is missing. {Required}",
        IngestRejection.MissingSessionId => $"sessionId is missing. {Required}",
        IngestRejection.MissingEventType => $"eventType is missing. {Required}",
        IngestRejection.FieldTooLong =>
            $"projectId, tenantId, sessionId, objectId and userId are limited to "
            + $"{IngestValidation.MaxIdentifierLength} characters, and eventType to "
            + $"{IngestValidation.MaxEventTypeLength}.",
        IngestRejection.TimestampTooFarAhead =>
            $"timestamp is more than {IngestValidation.MaxClockSkewAhead.TotalHours} hours in the "
            + "future. Past timestamps are accepted; a far-future one is a broken clock, and it "
            + "would land in a partition retention never reaches.",
        IngestRejection.ProjectNotPermittedForKey =>
            "this project key is not permitted to write to that projectId.",
        IngestRejection.EventTypeNotPermittedForKey =>
            "this key may not send that eventType: either the key lists the event types it may send, or "
            + "the event type is declared under TelemetryEngine:ServerEventTypes and only a server key may send it.",
        IngestRejection.ServerOnlyField =>
            "only a server key may set this field: a measure declared ServerOnly, or a trafficClass other "
            + "than normal on a public key.",
        IngestRejection.AliasWithoutIdentity =>
            "an alias event links its sessionId to a userId or an objectId, and this one carries neither.",
        _ => "accepted.",
    };
}

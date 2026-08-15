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
            CancellationToken cancellationToken) =>
        {
            string clientProjectKey = IngestValidation.ResolveProjectKey(
                context.Request.Headers["X-Project-Key"],
                context.Request.Query["k"]);

            if (clientProjectKey.Length == 0 || !keyValidator.IsValid(clientProjectKey))
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

            IngestRejection rejection = IngestValidation.Validate(payload, System.DateTime.UtcNow);

            // Checked here too, or a dry run would report "accepted" for a payload /track refuses —
            // which is the one thing this endpoint exists to make impossible.
            if (rejection == IngestRejection.None
                && !keyValidator.MayWriteProject(clientProjectKey, payload!.ProjectId))
            {
                rejection = IngestRejection.ProjectNotPermittedForKey;
            }

            if (rejection != IngestRejection.None)
            {
                return Results.Ok(new ValidateTelemetryResponse
                {
                    Accepted = false,
                    Reason = Explain(rejection),
                });
            }

            // Validate returning None means the payload is not null, but that is a fact about the
            // enum rather than something the compiler can see. Narrowed once, here, instead of a
            // null-forgiving operator at each of the four later uses.
            GlobalTelemetryPayload accepted = payload!;

            // The reported set comes out of the sanitizer that did the removing. Computing it a
            // second time here would make this endpoint's whole purpose -- telling you what the
            // real ingest path does with your payload -- rest on two implementations agreeing.
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

    /// <summary>
    /// Says which check refused the payload and what to do about it. The point of a dry run is that
    /// one curl tells you why, so "invalid" on its own would defeat the endpoint.
    /// </summary>
    private static string Explain(IngestRejection rejection) => rejection switch
    {
        // Names the field that stopped it AND the whole required set. Only the first would cost a
        // caller wiring this up one round trip per missing field; only the set would leave them to
        // work out which one they actually missed.
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
        _ => "accepted.",
    };
}

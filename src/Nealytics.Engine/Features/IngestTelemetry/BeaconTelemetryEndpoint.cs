namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;

public static class BeaconTelemetryEndpoint
{
    public static void MapBeaconIngestion(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/telemetry/beacon", async (
            HttpContext context,
            TelemetryChannelBroker broker,
            WriteAheadLogger wal,
            ApiKeyValidator keyValidator,
            IOptions<TelemetryEngineOptions> options) =>
        {
            using Activity? activity = TelemetryDiagnostics.Source.StartActivity("BeaconIngest");

            StringValues queryKey = context.Request.Query["k"];
            string clientProjectKey = queryKey.Count > 0 ? queryKey[0]! : string.Empty;

            if (clientProjectKey.Length == 0 || !keyValidator.IsValid(clientProjectKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (context.Request.ContentLength > options.Value.MaxRequestBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            try
            {
                PipeReader bodyReader = context.Request.BodyReader;
                int accepted = 0;

                await foreach (GlobalTelemetryPayload? payload in
                    JsonSerializer.DeserializeAsyncEnumerable(
                        bodyReader,
                        TelemetryAotContext.Default.GlobalTelemetryPayload,
                        context.RequestAborted))
                {
                    if (payload is null
                        || payload.ProjectId.Length == 0
                        || payload.TenantId.Length == 0
                        || payload.SessionId.Length == 0
                        || payload.EventType.Length == 0)
                    {
                        continue;
                    }

                    await wal.AppendAsync(payload, context.RequestAborted);
                    await broker.PublishAsync(payload, context.RequestAborted);
                    accepted++;
                }

                activity?.SetTag("neavents.beacon_events", accepted);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
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

namespace Nealytics.Engine.Features.IngestTelemetry;

using System.Buffers;
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

public static class IngestTelemetryEndpoint
{
    public static void MapTelemetryIngestion(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/telemetry/track", async (
            HttpContext context,
            TelemetryChannelBroker broker,
            WriteAheadLogger wal,
            ApiKeyValidator keyValidator,
            IOptions<TelemetryEngineOptions> options) =>
        {
            using Activity? activity = TelemetryDiagnostics.Source.StartActivity("IngestHttpRequest");

            StringValues headerKey = context.Request.Headers["X-Project-Key"];
            string clientProjectKey = headerKey.Count > 0 ? headerKey[0]! : string.Empty;

            if (clientProjectKey.Length == 0)
            {
                StringValues queryKey = context.Request.Query["k"];
                clientProjectKey = queryKey.Count > 0 ? queryKey[0]! : string.Empty;
            }

            if (clientProjectKey.Length == 0 || !keyValidator.IsValid(clientProjectKey))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            if (context.Request.ContentLength > options.Value.MaxRequestBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            try
            {
                PipeReader bodyReader = context.Request.BodyReader;
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

                if (payload is null
                    || string.IsNullOrEmpty(payload.ProjectId)
                    || string.IsNullOrEmpty(payload.TenantId)
                    || string.IsNullOrEmpty(payload.SessionId)
                    || string.IsNullOrEmpty(payload.EventType))
                {
                    return Results.BadRequest();
                }

                await wal.AppendAsync(payload, context.RequestAborted);
                await broker.PublishAsync(payload, context.RequestAborted);

                return Results.Accepted();
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
        })
        .RequireRateLimiting("ingestion");
    }
}

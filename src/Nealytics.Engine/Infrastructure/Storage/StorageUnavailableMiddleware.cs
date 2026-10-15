namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Diagnostics;

public sealed partial class StorageUnavailableMiddleware
{
    public const int RetryAfterSeconds = 5;

    private const string Body = "{\"type\":\"storage_unavailable\",\"title\":\"Storage is unavailable or overloaded; retry shortly.\",\"status\":503}";

    private readonly RequestDelegate _next;
    private readonly ILogger<StorageUnavailableMiddleware> _logger;

    public StorageUnavailableMiddleware(RequestDelegate next, ILogger<StorageUnavailableMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    [LoggerMessage(EventId = 9301, Level = LogLevel.Warning,
        Message = "Answered {Path} with 503: storage refused or dropped the read.")]
    private static partial void LogStorageUnavailable(ILogger logger, string path, Exception exception);

    public async Task InvokeAsync(HttpContext context, ClickHouseConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(connections);

        try
        {
            await _next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted
                                          && !context.RequestAborted.IsCancellationRequested
                                          && ClickHouseFault.IsTransient(exception))
        {
            LogStorageUnavailable(_logger, context.Request.Path.Value ?? string.Empty, exception);
            TelemetryDiagnostics.ReadsRefusedStorageUnavailable.Add(1);
            if (ClickHouseFault.IsConnectionFault(exception))
            {
                connections.EvictIdle();
            }

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(Body);
        }
    }
}

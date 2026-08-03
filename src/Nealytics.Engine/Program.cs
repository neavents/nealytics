using System.Collections.Generic;
using OpenTelemetry.Resources;
using OpenTelemetry.Exporter;
using System;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.GetActiveUsers;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetEventTimeSeries;
using Nealytics.Engine.Features.GetProjectTimeline;
using Nealytics.Engine.Features.GetSessionAnalytics;
using Nealytics.Engine.Features.GetTopEvents;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Json;
using Serilog.Sinks.OpenTelemetry;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

IConfigurationSection configSection = builder.Configuration.GetSection("TelemetryEngine");
builder.Services.Configure<TelemetryEngineOptions>(configSection);
TelemetryEngineOptions engineOpts = configSection.Get<TelemetryEngineOptions>() ?? new TelemetryEngineOptions();

if (string.IsNullOrWhiteSpace(engineOpts.JwtSymmetricKey) || Encoding.UTF8.GetByteCount(engineOpts.JwtSymmetricKey) < 32)
{
    throw new InvalidOperationException("TelemetryEngine:JwtSymmetricKey must be at least 32 bytes.");
}

// Built eagerly, before anything can take traffic, because an invalid declaration throws here and
// a refused boot is the only safe answer. The alternative — resolving it lazily on first ingest —
// would let the service report healthy and then fail one request at a time.
//
// The engine ships with this list empty. What is in it comes from the deployment's configuration,
// which is what makes this repo free of any one customer's vocabulary.
DimensionRegistry dimensionRegistry = new(engineOpts);
builder.Services.AddSingleton(dimensionRegistry);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = engineOpts.MaxRequestBodyBytes;
    if (engineOpts.MaxConcurrentConnections > 0)
    {
        serverOptions.Limits.MaxConcurrentConnections = engineOpts.MaxConcurrentConnections;
        serverOptions.Limits.MaxConcurrentUpgradedConnections = engineOpts.MaxConcurrentConnections;
    }
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, TelemetryAotContext.Default);
});

// The log pipeline had no exporter at all.
//
// Traces and metrics reach the collector through AddOpenTelemetry further down; logs went to the
// console and nowhere else. Nothing about that is visible: a service that exports no logs looks
// exactly like a service with nothing to report, and this one is the analytics ingest path, so
// the records that never left cover every beacon this estate receives.
//
// Confirmed live on 2026-07-31 — 8,670 spans from Nealytics.Engine in two hours and not one log
// line in the store. It was the last of the ten services still in that state.
var nealyticsOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

var nealyticsLogConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(new JsonFormatter());

if (!string.IsNullOrWhiteSpace(nealyticsOtlpEndpoint))
{
    nealyticsLogConfig = nealyticsLogConfig.WriteTo.OpenTelemetry(otlp =>
    {
        otlp.Endpoint = nealyticsOtlpEndpoint;

        // Explicit rather than inherited. Serilog's sink does read OTEL_EXPORTER_OTLP_PROTOCOL, but
        // only after this action runs and without saying so anywhere, so a deployment that sets the
        // endpoint under a different key — which most of this estate does — silently falls back to
        // gRPC against an HTTP/protobuf port and delivers nothing. The port is the one fact that
        // cannot disagree with the endpoint: 4317 IS gRPC, 4318 IS http/protobuf.
        otlp.Protocol =
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")?.Trim().ToLowerInvariant() switch
            {
                "grpc" => OtlpProtocol.Grpc,
                "http/protobuf" => OtlpProtocol.HttpProtobuf,
                _ => nealyticsOtlpEndpoint.Contains(":4317", StringComparison.Ordinal)
                    ? OtlpProtocol.Grpc
                    : OtlpProtocol.HttpProtobuf,
            };

        // Must match the serviceName the tracer registers below, exactly. The two pipelines build
        // separate resources and share nothing, so a name set in one place and not the other files
        // the logs under unknown_service:dotnet — delivered, stored, and attributed to nothing.
        otlp.ResourceAttributes = new Dictionary<string, object>
        {
            ["service.name"] = "Nealytics.Engine",
            ["deployment.environment"] = builder.Environment.EnvironmentName,
        };
    });
}

Log.Logger = nealyticsLogConfig.CreateLogger();

// Serilog owns the log pipeline outright here and exports OTLP itself through the sink above, so
// there is no second ILoggerProvider to forward to and no writeToProviders question to get wrong.
// That question is real — its default of false is exactly what left the sibling dracula service
// exporting nothing, under a comment claiming the bridge was connected — but the way to not get it
// wrong is to not depend on it.
builder.Host.UseSerilog(Log.Logger, dispose: true);

builder.Services.AddCors(cors =>
{
    cors.AddPolicy("beacon", policy =>
    {
        if (string.IsNullOrWhiteSpace(engineOpts.CorsAllowedOrigins))
        {
            policy.AllowAnyOrigin();
        }
        else
        {
            string[] origins = engineOpts.CorsAllowedOrigins
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            policy.WithOrigins(origins);
        }

        policy.WithMethods("POST")
            .WithHeaders("Content-Type")
            .SetPreflightMaxAge(TimeSpan.FromHours(24));
    });
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(engineOpts.JwtSymmetricKey)),
            ClockSkew = TimeSpan.FromSeconds(engineOpts.JwtClockSkewSeconds)
        };
    });
builder.Services.AddAuthorization();

if (engineOpts.EnableRequestDecompression)
{
    builder.Services.AddRequestDecompression();
}

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddFixedWindowLimiter("ingestion", window =>
    {
        window.PermitLimit = engineOpts.RateLimitPermitCount;
        window.Window = TimeSpan.FromSeconds(engineOpts.RateLimitWindowSeconds);
        window.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        window.QueueLimit = engineOpts.RateLimitQueueSize;
    });
});

builder.Services.AddSingleton<ClickHouseConnectionFactory>();
builder.Services.AddSingleton<WriteAheadLogger>();
builder.Services.AddSingleton<TelemetryChannelBroker>();
builder.Services.AddSingleton<ApiKeyValidator>();
builder.Services.AddSingleton<DimensionSanitizer>();
// Widens global_events before the batch processor takes traffic. clickhouse-init.sql only runs
// on an empty volume, so an existing deployment would otherwise reject every batch naming a
// column added since it was first created.
builder.Services.AddHostedService<ClickHouseSchemaMigrator>();
builder.Services.AddSingleton<ITelemetryBatchWriter, ClickHouseBatchWriter>();
builder.Services.AddHostedService<TelemetryBatchProcessor>();

builder.Services.AddScoped<GetProjectTimelineQuery>();
builder.Services.AddScoped<GetSessionAnalyticsQuery>();
builder.Services.AddScoped<GetEventTimeSeriesQuery>();
builder.Services.AddScoped<GetActiveUsersQuery>();
builder.Services.AddScoped<GetTopEventsQuery>();
builder.Services.AddScoped<GetBreakdownQuery>();
// The query allowlist. A singleton built from the registry, so groupBy/filter validation and the
// schema reconciler can never disagree about which dimensions exist.
builder.Services.AddSingleton(new BreakdownColumns(dimensionRegistry));

// Where telemetry actually goes, and it went nowhere before this.
//
// AddOtlpExporter() with no configuration uses the SDK defaults: http://localhost:4317 over gRPC.
// Inside a container localhost is the container itself, so every span and metric was posted to a port
// nothing was listening on — silently, because the exporter retries in the background and logs at
// debug. nealytics had no OTEL variables in docker-compose either, so nothing overrode it.
//
// The endpoint is read from OTEL_EXPORTER_OTLP_ENDPOINT, which is what the rest of the estate is given
// and what compose now supplies. The protocol is resolved rather than defaulted: the collector is
// addressed on :4318 everywhere here, and gRPC to an HTTP/protobuf port delivers nothing. That exact
// mismatch shipped in identity, messaging, subscription and neaslator — the endpoint and the protocol
// are set in different places, and neither looks wrong on its own.
static void ConfigureOtlp(OtlpExporterOptions options)
{
    var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
    if (!string.IsNullOrWhiteSpace(endpoint))
    {
        options.Endpoint = new Uri(endpoint);
    }

    options.Protocol =
        Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")?.Trim().ToLowerInvariant() == "grpc"
            ? OtlpExportProtocol.Grpc
            : OtlpExportProtocol.HttpProtobuf;
}

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        // Without these a span arrives and cannot be attributed to anything. service.name in
        // particular is what SigNoz groups by, so its absence makes traces effectively invisible even
        // when they are delivered.
        .AddService(
            serviceName: "Nealytics.Engine",
            serviceVersion: "1.0",
            serviceInstanceId: Environment.MachineName)
        .AddAttributes([
            new KeyValuePair<string, object>(
                "deployment.environment",
                builder.Environment.EnvironmentName),
        ]))
    .WithTracing(tracing => tracing
        .AddSource(TelemetryDiagnostics.Source.Name)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter(ConfigureOtlp))
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(TelemetryDiagnostics.EngineMeter.Name)
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter(ConfigureOtlp);

        if (engineOpts.EnablePrometheusScrape)
        {
            metrics.AddPrometheusExporter();
        }
    });

WebApplication app = builder.Build();

app.Use(async (HttpContext context, RequestDelegate next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "0";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next(context);
});

if (engineOpts.EnableRequestDecompression)
{
    app.UseRequestDecompression();
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapTelemetryIngestion();
app.MapBeaconIngestion();
app.MapGetProjectTimeline();
app.MapGetSessionAnalytics();
app.MapGetEventTimeSeries();
app.MapGetActiveUsers();
app.MapGetTopEvents();
app.MapGetBreakdown();
if (engineOpts.EnablePrometheusScrape)
{
    app.MapPrometheusScrapingEndpoint();
}

app.MapGet("/health", () => Results.Ok());

app.MapGet("/ready", async (ClickHouseConnectionFactory connectionFactory) =>
{
    try
    {
        await using PooledClickHouseConnection lease =
            await connectionFactory.AcquireAsync(CancellationToken.None);
        await using ClickHouseCommand cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(CancellationToken.None);
        return Results.Ok();
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

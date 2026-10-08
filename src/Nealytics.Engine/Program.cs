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
using Nealytics.Engine.Features.GetDistribution;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Features.GetFunnel;
using Nealytics.Engine.Features.GetSchema;
using Nealytics.Engine.Features.ValidateTelemetry;
using Nealytics.Engine.Features.GetEventTimeSeries;
using Nealytics.Engine.Features.GetProjectTimeline;
using Nealytics.Engine.Features.GetRetention;
using Nealytics.Engine.Features.GetSessionAnalytics;
using Nealytics.Engine.Features.GetTopEvents;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Features.UpsertTenantAttributes;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Json;
using Serilog.Sinks.OpenTelemetry;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string? schemaFile = builder.Configuration["TelemetryEngine:SchemaFile"];

if (!string.IsNullOrWhiteSpace(schemaFile))
{
    builder.Configuration.AddJsonFile(schemaFile, optional: true, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
}

IConfigurationSection configSection = builder.Configuration.GetSection("TelemetryEngine");
builder.Services.Configure<TelemetryEngineOptions>(configSection);
TelemetryEngineOptions engineOpts = configSection.Get<TelemetryEngineOptions>() ?? new TelemetryEngineOptions();

ReadTokenKeys readTokenKeys = ReadTokenKeys.From(engineOpts);

DimensionRegistry dimensionRegistry = new(engineOpts);
builder.Services.AddSingleton(dimensionRegistry);

MeasureRegistry measureRegistry = new(engineOpts, dimensionRegistry);
builder.Services.AddSingleton(measureRegistry);

TenantAttributeRegistry tenantAttributeRegistry = new(engineOpts);
builder.Services.AddSingleton(tenantAttributeRegistry);

RollupRegistry rollupRegistry = new(engineOpts, dimensionRegistry, measureRegistry, tenantAttributeRegistry);
builder.Services.AddSingleton(rollupRegistry);

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

string? nealyticsOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

static LogEventLevel NealyticsLevel(string? configured, LogEventLevel fallback) => configured switch
{
    "Trace" => LogEventLevel.Verbose,
    "Debug" => LogEventLevel.Debug,
    "Information" => LogEventLevel.Information,
    "Warning" => LogEventLevel.Warning,
    "Error" => LogEventLevel.Error,
    "Critical" => LogEventLevel.Fatal,
    "None" => LogEventLevel.Fatal,
    _ => fallback,
};

LoggerConfiguration nealyticsLogConfig = new LoggerConfiguration()
    .MinimumLevel.Is(NealyticsLevel(
        builder.Configuration["Logging:LogLevel:Default"], LogEventLevel.Information))
    .MinimumLevel.Override("Microsoft.AspNetCore", NealyticsLevel(
        builder.Configuration["Logging:LogLevel:Microsoft.AspNetCore"], LogEventLevel.Warning))
    .Enrich.FromLogContext()
    .WriteTo.Console(new JsonFormatter());

if (!string.IsNullOrWhiteSpace(nealyticsOtlpEndpoint))
{
    nealyticsLogConfig = nealyticsLogConfig.WriteTo.OpenTelemetry(otlp =>
    {
        otlp.Endpoint = nealyticsOtlpEndpoint;

        otlp.Protocol =
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")?.Trim().ToLowerInvariant() switch
            {
                "grpc" => OtlpProtocol.Grpc,
                "http/protobuf" => OtlpProtocol.HttpProtobuf,
                _ => nealyticsOtlpEndpoint.Contains(":4317", StringComparison.Ordinal)
                    ? OtlpProtocol.Grpc
                    : OtlpProtocol.HttpProtobuf,
            };

        otlp.ResourceAttributes = new Dictionary<string, object>
        {
            ["service.name"] = "Nealytics.Engine",
            ["deployment.environment"] = builder.Environment.EnvironmentName,
        };
    });
}

Log.Logger = nealyticsLogConfig.CreateLogger();

builder.Host.UseSerilog(Log.Logger, dispose: true);

builder.Services.AddCors(cors =>
{
    cors.AddPolicy("beacon", policy =>
    {
        if (string.IsNullOrWhiteSpace(engineOpts.CorsAllowedOrigins))
        {
            Log.Logger.Warning(
                "TelemetryEngine:CorsAllowedOrigins is not set, so the beacon endpoint accepts a "
                + "cross-origin POST from anywhere. Set it to the origins that serve your pages.");

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

JwksKeyCache? jwksKeyCache = readTokenKeys.CreateJwksCache(
    engineOpts, new SerilogLoggerFactory(Log.Logger).CreateLogger(nameof(JwksKeyCache)));

if (jwksKeyCache is not null)
{
    builder.Services.AddHostedService(_ => jwksKeyCache);
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => readTokenKeys.Configure(options, engineOpts));
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
builder.Services.AddSingleton<IngestGate>();
builder.Services.AddSingleton<ITenantAttributeWriter, ClickHouseTenantAttributeWriter>();
builder.Services.AddSingleton<DimensionSanitizer>();
builder.Services.AddSingleton<MeasureSanitizer>();
builder.Services.AddHostedService<ClickHouseSchemaMigrator>();
builder.Services.AddSingleton<ITelemetryBatchWriter, ClickHouseBatchWriter>();
builder.Services.AddHostedService<TelemetryBatchProcessor>();

builder.Services.AddScoped<GetProjectTimelineQuery>();
builder.Services.AddScoped<GetSessionAnalyticsQuery>();
builder.Services.AddScoped<GetEventTimeSeriesQuery>();
builder.Services.AddScoped<GetActiveUsersQuery>();
builder.Services.AddScoped<GetTopEventsQuery>();
builder.Services.AddScoped<GetBreakdownQuery>();
builder.Services.AddScoped<GetFunnelQuery>();
builder.Services.AddScoped<GetPivotQuery>();
builder.Services.AddScoped<GetDistributionQuery>();
builder.Services.AddScoped<GetRetentionQuery>();
builder.Services.AddSingleton<GetEventTypesQuery>();
builder.Services.AddSingleton<GetColumnPopulationQuery>();
builder.Services.AddSingleton(new QueryColumns(dimensionRegistry, tenantAttributeRegistry));

static void ConfigureOtlp(OtlpExporterOptions options)
{
    string? endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
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
app.MapGetSchema();
app.MapGetFunnel();
app.MapGetPivot();
app.MapGetDistribution();
app.MapGetRetention();
app.MapUpsertTenantAttributes();
app.MapValidateTelemetry();
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

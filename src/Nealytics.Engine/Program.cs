using Microsoft.Extensions.Options;
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
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.KafkaIngestion;
using Nealytics.Engine.Features.GetActiveUsers;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetDistribution;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Features.GetFunnel;
using Nealytics.Engine.Features.GetSchema;
using Nealytics.Engine.Features.ValidateTelemetry;
using Nealytics.Engine.Features.GetEventTimeSeries;
using Nealytics.Engine.Features.GetNamedMetric;
using Nealytics.Engine.Features.GetSavedReport;
using Nealytics.Engine.Features.GetAnalyticsPlan;
using Nealytics.Engine.Features.GetProjectTimeline;
using Nealytics.Engine.Features.GetRetention;
using Nealytics.Engine.Features.GetSessionAnalytics;
using Nealytics.Engine.Features.GetTopEvents;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Features.ImportCsv;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.S3Import;
using Nealytics.Engine.Features.SourceMapping;
using Nealytics.Engine.Features.UpsertTenantAttributes;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Delivery;
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

NamedMetricRegistry namedMetricRegistry = new(engineOpts, measureRegistry);
builder.Services.AddSingleton(namedMetricRegistry);

TenantAttributeRegistry tenantAttributeRegistry = new(engineOpts);
builder.Services.AddSingleton(tenantAttributeRegistry);

ComputedDimensionRegistry computedDimensionRegistry = new(engineOpts, dimensionRegistry, measureRegistry);
builder.Services.AddSingleton(computedDimensionRegistry);
QueryColumns legacyColumns = new(dimensionRegistry, tenantAttributeRegistry);
builder.Services.AddSingleton(legacyColumns);
QueryColumns queryColumns = new(dimensionRegistry, tenantAttributeRegistry, computedDimensionRegistry);
builder.Services.AddSingleton(new AnalyticsColumns(queryColumns));

EventSegmentRegistry eventSegmentRegistry = new(engineOpts, queryColumns, measureRegistry);
builder.Services.AddSingleton(eventSegmentRegistry);

SavedReportRegistry savedReportRegistry = new(engineOpts, namedMetricRegistry, eventSegmentRegistry);
builder.Services.AddSingleton(savedReportRegistry);

RollupRegistry rollupRegistry = new(engineOpts, dimensionRegistry, measureRegistry, tenantAttributeRegistry);
builder.Services.AddSingleton(rollupRegistry);

ProjectContext defaultProject = new(null, dimensionRegistry, measureRegistry, tenantAttributeRegistry,
    computedDimensionRegistry, legacyColumns, new AnalyticsColumns(queryColumns), namedMetricRegistry,
    eventSegmentRegistry, savedReportRegistry, rollupRegistry);
ProjectCatalog projectCatalog = new(engineOpts, defaultProject);
builder.Services.AddSingleton(projectCatalog);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ProjectRequestContext>();
if (projectCatalog.Enabled)
{
    builder.Services.AddScoped(services => services.GetRequiredService<ProjectRequestContext>().Current.Dimensions);
    builder.Services.AddScoped(services => services.GetRequiredService<ProjectRequestContext>().Current.Measures);
    builder.Services.AddScoped(services => services.GetRequiredService<ProjectRequestContext>().Current.TenantAttributes);
    builder.Services.AddScoped(services => services.GetRequiredService<ProjectRequestContext>().Current.ComputedDimensions);
    builder.Services.AddScoped(services => services.GetRequiredService<ProjectRequestContext>().Current.Columns);
    builder.Services.AddScoped(services => services.GetRequiredService<ProjectRequestContext>().Current.Analytics);
    builder.Services.AddScoped(services => services.GetRequiredService<ProjectRequestContext>().Current.NamedMetrics);
    builder.Services.AddScoped(services => services.GetRequiredService<ProjectRequestContext>().Current.EventSegments);
    builder.Services.AddScoped(services => services.GetRequiredService<ProjectRequestContext>().Current.SavedReports);
    builder.Services.AddScoped(services => services.GetRequiredService<ProjectRequestContext>().Current.Rollups);
}


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

builder.Services.AddRateLimiter();
builder.Services.AddOptions<RateLimiterOptions>().Configure<IOptions<TelemetryEngineOptions>>((limiter, configured) =>
{
    TelemetryEngineOptions capacityOptions = configured.Value;
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.GlobalLimiter = IngestionRequestAdmission.Create(capacityOptions);
    limiter.AddFixedWindowLimiter("ingestion", window =>
    {
        window.PermitLimit = capacityOptions.RateLimitPermitCount;
        window.Window = TimeSpan.FromSeconds(capacityOptions.RateLimitWindowSeconds);
        window.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        window.QueueLimit = capacityOptions.RateLimitQueueSize;
    });
});

if (engineOpts.EnableNdjsonImport || engineOpts.EnableCsvImport)
{
    NdjsonImportLimits importLimits = NdjsonImportLimits.Create(engineOpts);
    builder.Services.AddSingleton(importLimits);
    builder.Services.Configure<RateLimiterOptions>(options =>
        options.AddConcurrencyLimiter("ndjson-import", limiter =>
        {
            limiter.PermitLimit = importLimits.Concurrency;
            limiter.QueueLimit = 0;
        }));
}

if (engineOpts.EnableNdjsonImport)
{
    builder.Services.AddSingleton(new NdjsonSourceMappings(engineOpts, projectCatalog));
    builder.Services.AddScoped<NdjsonImporter>();
}
if (engineOpts.EnableCsvImport)
{
    builder.Services.AddSingleton(new CsvSourceMappings(engineOpts, projectCatalog));
    builder.Services.AddScoped<CsvImporter>();
}

builder.Services.AddSingleton<ClickHouseConnectionFactory>();
if (engineOpts.EnableImportJobs)
{
    ImportJobLimits jobLimits = ImportJobLimits.Create(engineOpts);
    builder.Services.AddSingleton(jobLimits);
    builder.Services.AddSingleton(new ImportJobContracts(engineOpts, projectCatalog, jobLimits));
    builder.Services.AddSingleton(services => new ImportJobStore(new ImportJobJournal(
        services.GetRequiredService<IOptions<TelemetryEngineOptions>>().Value.WriteAheadLogDirectory), jobLimits.Jobs));
    builder.Services.AddHostedService(services => services.GetRequiredService<ImportJobStore>());
    builder.Services.AddSingleton<ImportJobCoordinator>();
}
builder.Services.AddSingleton<WriteAheadLogger>();
S3Sources s3Sources = new(engineOpts, projectCatalog);
if (s3Sources.Count != 0)
{
    builder.Services.AddSingleton(s3Sources);
    builder.Services.AddSingleton<S3ObjectClient>();
    builder.Services.AddSingleton<S3ImportCoordinator>();
}
builder.Services.AddSingleton<TelemetryChannelBroker>();
builder.Services.AddSingleton<ApiKeyValidator>();
builder.Services.AddSingleton<ITenantAttributeWriter, ClickHouseTenantAttributeWriter>();
if (projectCatalog.Enabled)
{
    builder.Services.AddScoped<IngestGate>();
    builder.Services.AddScoped<DimensionSanitizer>();
    builder.Services.AddScoped<MeasureSanitizer>();
    builder.Services.AddScoped<BeaconBatchIngestor>();
}
else
{
    builder.Services.AddSingleton<IngestGate>();
    builder.Services.AddSingleton<DimensionSanitizer>();
    builder.Services.AddSingleton<MeasureSanitizer>();
    builder.Services.AddSingleton<BeaconBatchIngestor>();
}
builder.Services.AddSingleton<IngestionRequestReader>();
builder.Services.AddSingleton<DurableIngestDispatcher>();
if (projectCatalog.Enabled)
{
    StorageSchema storage = projectCatalog.Storage;
    builder.Services.AddHostedService(services => new ClickHouseSchemaMigrator(
        services.GetRequiredService<ClickHouseConnectionFactory>(), storage.Dimensions, storage.Measures, storage.Rollups,
        services.GetRequiredService<IOptions<TelemetryEngineOptions>>(), services.GetRequiredService<ILogger<ClickHouseSchemaMigrator>>(),
        storage.TenantAttributes));
    builder.Services.AddSingleton(services => new ClickHouseBatchWriter(
        services.GetRequiredService<ClickHouseConnectionFactory>(), storage.Dimensions, storage.Measures,
        services.GetRequiredService<IOptions<TelemetryEngineOptions>>(), services.GetRequiredService<ILogger<ClickHouseBatchWriter>>()));
}
else
{
    builder.Services.AddHostedService<ClickHouseSchemaMigrator>();
    builder.Services.AddSingleton<ClickHouseBatchWriter>();
}
builder.Services.AddSingleton<IDeliveryBatchWriter>(services => services.GetRequiredService<ClickHouseBatchWriter>());
builder.Services.AddSingleton<TelemetryDeliveryJournal>();
if (projectCatalog.Enabled)
{
    builder.Services.AddSingleton<ITelemetryBatchWriter>(services => new JournaledTelemetryBatchWriter(
        services.GetRequiredService<IDeliveryBatchWriter>(), services.GetRequiredService<TelemetryDeliveryJournal>(),
        services.GetRequiredService<WriteAheadLogger>(), services.GetRequiredService<IOptions<TelemetryEngineOptions>>(),
        projectCatalog.Storage.Rollups));
}
else
{
    builder.Services.AddSingleton<ITelemetryBatchWriter, JournaledTelemetryBatchWriter>();
}
builder.Services.AddHostedService<TelemetryBatchProcessor>();

builder.Services.AddKafkaConnector(engineOpts, projectCatalog);

builder.Services.AddScoped<GetProjectTimelineQuery>();
builder.Services.AddScoped<GetSessionAnalyticsQuery>();
builder.Services.AddScoped<GetEventTimeSeriesQuery>();
builder.Services.AddScoped<GetNamedMetricQuery>();
builder.Services.AddScoped<GetSavedReportQuery>();
builder.Services.AddScoped<AnalyticsPlanner>();
builder.Services.AddScoped<GetActiveUsersQuery>();
builder.Services.AddScoped<GetTopEventsQuery>();
builder.Services.AddScoped<GetBreakdownQuery>();
builder.Services.AddScoped<GetFunnelQuery>();
builder.Services.AddScoped<GetPivotQuery>();
builder.Services.AddScoped<GetDistributionQuery>();
builder.Services.AddScoped<GetRetentionQuery>();
builder.Services.AddSingleton<GetEventTypesQuery>();
if (projectCatalog.Enabled)
{
    builder.Services.AddScoped<GetColumnPopulationQuery>();
}
else
{
    builder.Services.AddSingleton<GetColumnPopulationQuery>();
}

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
app.UseMiddleware<StorageUnavailableMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseProjectSchemas();
app.UseAuthorization();

if (engineOpts.EnableCsvImport)
{
    app.MapCsvImport();
}
app.MapTelemetryIngestion();
app.MapBeaconIngestion();
if (engineOpts.EnableNdjsonImport)
{
    app.MapNdjsonImport();
}
if (engineOpts.EnableImportJobs)
{
    app.MapImportJobs();
}
if (s3Sources.Count != 0)
{
    app.MapS3Import();
}
app.MapGetProjectTimeline();
app.MapGetSessionAnalytics();
app.MapGetEventTimeSeries();
app.MapGetNamedMetric();
app.MapGetSavedReport();
app.MapGetAnalyticsPlan();
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

app.MapGet("/ready", async (HttpContext context, ClickHouseConnectionFactory connectionFactory) =>
{
    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    timeout.CancelAfter(connectionFactory.ProbeTimeout);
    try
    {
        await using PooledClickHouseConnection lease =
            await connectionFactory.AcquireAsync(ClickHouseWorkload.Probe, timeout.Token);
        await using ClickHouseCommand cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(timeout.Token);
        return Results.Ok();
    }
    catch (Exception)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

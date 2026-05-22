using System;
using System.Text;
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
using Nealytics.Engine.Features.GetProjectTimeline;
using Nealytics.Engine.Features.GetSessionAnalytics;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Json;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

IConfigurationSection configSection = builder.Configuration.GetSection("TelemetryEngine");
builder.Services.Configure<TelemetryEngineOptions>(configSection);
TelemetryEngineOptions engineOpts = configSection.Get<TelemetryEngineOptions>() ?? new TelemetryEngineOptions();

if (string.IsNullOrWhiteSpace(engineOpts.JwtSymmetricKey) || Encoding.UTF8.GetByteCount(engineOpts.JwtSymmetricKey) < 32)
{
    throw new InvalidOperationException("TelemetryEngine:JwtSymmetricKey must be at least 32 bytes.");
}

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = engineOpts.MaxRequestBodyBytes;
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, TelemetryAotContext.Default);
});

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(new JsonFormatter())
    .CreateLogger();
builder.Host.UseSerilog();

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
builder.Services.AddHostedService<TelemetryBatchProcessor>();

builder.Services.AddScoped<GetProjectTimelineQuery>();
builder.Services.AddScoped<GetSessionAnalyticsQuery>();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(TelemetryDiagnostics.Source.Name)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(TelemetryDiagnostics.EngineMeter.Name)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

WebApplication app = builder.Build();

app.Use(async (HttpContext context, RequestDelegate next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "0";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next(context);
});

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapTelemetryIngestion();
app.MapBeaconIngestion();
app.MapGetProjectTimeline();
app.MapGetSessionAnalytics();
app.MapGet("/health", () => Results.Ok());

app.Run();

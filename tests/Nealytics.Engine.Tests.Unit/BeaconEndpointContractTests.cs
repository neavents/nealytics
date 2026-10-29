using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Recovery accounting")]
public sealed class BeaconEndpointContractTests
{
    private const string Event = """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e"}""";

    [Theory]
    [InlineData("", false)]
    [InlineData("?k=wrong", false)]
    [InlineData("?k=unit-key-1", true)]
    public async Task OnlyTheExistingQueryCredentialCanAuthorizeABeacon(string query, bool accepted)
    {
        await using Factory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/telemetry/beacon" + query)
        {
            Content = new StringContent("[" + Event + "]", Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("X-Project-Key", "unit-key-1");
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(accepted ? HttpStatusCode.NoContent : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(accepted ? 1 : 0, factory.Services.GetRequiredService<WriteAheadLogger>().UncommittedRecordCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task KnownAndUnknownBodyLengthsEnforceTheExactConfiguredByteBoundary(bool chunked, bool oversized)
    {
        await using Factory factory = new(128, kestrel: false);
        using HttpClient client = factory.CreateClient();
        string body = "[" + Event + "]";
        body += new string(' ', 128 - Encoding.UTF8.GetByteCount(body) + (oversized ? 1 : 0));
        using HttpContent content = chunked
            ? new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)))
            : new StringContent(body, Encoding.UTF8, "text/plain");
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/telemetry/beacon?k=unit-key-1")
        {
            Content = content
        };
        if (chunked)
        {
            request.Headers.TransferEncodingChunked = true;
        }
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(oversized ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CompletedBeaconsKeepTheirTraceCountsAndStructuredRejectionWarning(int rejected)
    {
        ConcurrentQueue<Activity> activities = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == "Nealytics.Engine.Core",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(listener);
        await using Factory factory = new();
        using HttpClient client = factory.CreateClient();
        ActivityTraceId traceId = ActivityTraceId.CreateRandom();
        string body = "[" + Event + string.Concat(Enumerable.Repeat(",null", rejected)) + "]";
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/telemetry/beacon?k=unit-key-1")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Activity activity = Assert.Single(activities, item => item.TraceId == traceId && item.OperationName == "BeaconIngest");
        Assert.Equal(1, activity.GetTagItem("nealytics.beacon_events"));
        Assert.Equal(rejected, activity.GetTagItem("nealytics.beacon_rejected"));
        Entry[] warnings = factory.Logs.Entries.Where(item => item.Event.Id == 9203).ToArray();
        if (rejected == 0)
        {
            Assert.Empty(warnings);
        }
        else
        {
            Entry warning = Assert.Single(warnings);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Equal("Nealytics.Engine.Features.IngestTelemetry.BeaconTelemetryEndpoint", warning.Category);
            Assert.Equal(rejected, warning.State["RejectedCount"]);
            Assert.Equal(rejected + 1, warning.State["TotalCount"]);
        }
    }

    [Fact]
    public async Task BeaconRetainsTheExistingLimiterAndCorsPolicies()
    {
        await using Factory factory = new();
        using HttpClient client = factory.CreateClient();
        RouteEndpoint endpoint = Assert.Single(factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>(), item => item.RoutePattern.RawText == "/api/v1/telemetry/beacon");
        Assert.Equal("ingestion", endpoint.Metadata.GetRequiredMetadata<EnableRateLimitingAttribute>().PolicyName);
        Assert.Equal("beacon", endpoint.Metadata.GetRequiredMetadata<IEnableCorsAttribute>().PolicyName);
    }

    private sealed class Factory : NoDatabaseWebFactory
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-beacon-http-" + Guid.NewGuid().ToString("N"));
        private readonly int _limit;

        public Factory(int limit = 1_048_576, bool kestrel = true)
        {
            _limit = limit;
            if (kestrel)
            {
                UseKestrel(0);
            }
        }

        public CaptureLogs Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILoggerFactory>(new LoggerFactory([Logs]));
                foreach (ServiceDescriptor descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)
                    && (item.ImplementationType == typeof(TelemetryBatchProcessor)
                        || item.ImplementationType == typeof(ClickHouseSchemaMigrator))).ToArray())
                {
                    services.Remove(descriptor);
                }
                services.PostConfigure<TelemetryEngineOptions>(options =>
                {
                    options.WriteAheadLogDirectory = _directory;
                    options.MaxRequestBodyBytes = _limit;
                });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
    }

    private sealed record Entry(string Category, LogLevel Level, EventId Event, Dictionary<string, object?> State);

    private sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<Entry> entries) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
                Func<TState, Exception?, string> formatter)
            {
                if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                {
                    entries.Enqueue(new Entry(category, level, eventId, fields.ToDictionary()));
                }
            }
        }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Recovery accounting")]
public sealed class IngestionAdmissionHttpTests
{
    [Theory]
    [InlineData("managed")]
    [InlineData("custom")]
    public async Task AdmissionIsHeldUntilADurableRecordHasCompletedItsChannelHandoff(string path)
    {
        await using Factory factory = new(path);
        using HttpClient client = factory.CreateClient();
        const string body = """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e"}""";
        using HttpResponseMessage first = await TrackAsync(client, body);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Task<HttpResponseMessage> blocked = TrackAsync(client, body);
        WriteAheadLogger wal = factory.Services.GetRequiredService<WriteAheadLogger>();
        await WaitForAsync(() => wal.UncommittedRecordCount == 2 && factory.Available == 0);
        using HttpResponseMessage refused = await TrackAsync(client, "not-json");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.False(blocked.IsCompleted);
        TelemetryChannelBroker broker = factory.Services.GetRequiredService<TelemetryChannelBroker>();
        Assert.True(broker.Reader.TryRead(out _));
        using HttpResponseMessage second = await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(1, factory.Available);
        Assert.Equal(2, wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task AStorageFailureReturnsAdmissionBeforeTheNextRequestIsParsed()
    {
        await using Factory factory = new("custom", failStorage: true);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage failed = await TrackAsync(client,
            """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e"}""");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        using HttpResponseMessage malformed = await TrackAsync(client, "not-json");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(1, factory.Available);
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("custom")]
    public async Task AStalledBodyRefusesAdditionalIngestionAndKeepsHealthAvailable(string path)
    {
        await using Factory factory = new(path);
        using HttpClient client = factory.CreateClient();
        using TcpClient stalled = new();
        await stalled.ConnectAsync(client.BaseAddress!.Host, client.BaseAddress.Port);
        string prefix = "POST /api/v1/telemetry/track?k=unit-key-1 HTTP/1.1\r\nHost: localhost\r\n"
            + "Content-Type: application/json\r\nContent-Length: 500\r\n\r\n{\"projectId\":\"p\",";
        await stalled.GetStream().WriteAsync(Encoding.ASCII.GetBytes(prefix));
        await WaitForAsync(() => factory.Available == 0);
        using HttpResponseMessage overloaded = await client.PostAsync("/api/v1/telemetry/beacon?k=unit-key-1",
            new StringContent("not-json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.TooManyRequests, overloaded.StatusCode);
        using HttpResponseMessage health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        stalled.Close();
        await WaitForAsync(() => factory.Available == 1);
        using HttpResponseMessage accepted = await TrackAsync(client, """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e"}""");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(1, factory.Services.GetRequiredService<WriteAheadLogger>().UncommittedRecordCount);
    }

    [Theory]
    [InlineData("managed", false)]
    [InlineData("managed", true)]
    [InlineData("custom", false)]
    [InlineData("custom", true)]
    public async Task RejectedBodiesReleaseAdmissionForTheNextRequest(string path, bool oversized)
    {
        await using Factory factory = new(path);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage rejected = await TrackAsync(client, oversized ? new string('x', 2_048) : "not-json");
        Assert.Equal(oversized ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest, rejected.StatusCode);
        using HttpResponseMessage accepted = await TrackAsync(client, """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e"}""");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(1, factory.Available);
    }

    private static Task<HttpResponseMessage> TrackAsync(HttpClient client, string body) =>
        client.PostAsync("/api/v1/telemetry/track?k=unit-key-1", new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(2, deadline.Token);
        }
    }

    private sealed class Factory : NoDatabaseWebFactory
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-admission-" + Guid.NewGuid().ToString("N"));
        private readonly string _path;
        private readonly bool _failStorage;
        private readonly PartitionedRateLimiter<HttpContext> _admission = IngestionRequestAdmission.Create(new TelemetryEngineOptions
        {
            MaxConcurrentIngestionRequests = 1,
            MaxRequestBodyBytes = 1_024,
            IngestionRequestByteBudget = 1_024
        });

        public Factory(string path, bool failStorage = false)
        {
            _path = path;
            _failStorage = failStorage;
            UseKestrel(0);
        }

        public long? Available => _admission.GetStatistics(IngestionRequestAdmissionTests.Context(ClickHouseWorkload.Write))?.CurrentAvailablePermits;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                foreach (ServiceDescriptor descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)
                    && (item.ImplementationType == typeof(TelemetryBatchProcessor)
                        || item.ImplementationType == typeof(ClickHouseSchemaMigrator))).ToArray())
                {
                    services.Remove(descriptor);
                }
                services.PostConfigure<TelemetryEngineOptions>(options =>
                {
                    options.WriteAheadLogDirectory = _directory;
                    options.MaxRequestBodyBytes = 1_024;
                    options.IngestionExecutionPath = _path;
                    options.MemoryChannelCapacity = 1;
                });
                if (_failStorage)
                {
                    services.AddSingleton(provider => new WriteAheadLogger(
                        provider.GetRequiredService<IOptions<TelemetryEngineOptions>>(), new FailingFile()));
                }
                services.PostConfigure<RateLimiterOptions>(options =>
                {
                    options.GlobalLimiter?.Dispose();
                    options.GlobalLimiter = _admission;
                });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            _admission.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
    }

    private sealed class FailingFile : IWalFile
    {
        public ValueTask AppendAsync(ReadOnlyMemory<byte> bytes) => ValueTask.FromException(new IOException("injected storage failure"));
        public ValueTask FlushAsync() => ValueTask.CompletedTask;
        public ValueTask TruncateAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

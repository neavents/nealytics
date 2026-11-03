using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Nealytics.Engine.Features.ImportNdjson;
using System.Net.Http.Headers;
using System.IO.Compression;
using Nealytics.Engine.Features.IngestTelemetry;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Project context hosts")]
public sealed class NdjsonImportHttpTests : IClassFixture<NdjsonImportHttpTests.Factory>
{
    private readonly Factory _factory;
    private readonly HttpClient _client;
    internal const string Route = "/api/v1/telemetry/import/ndjson";

    public NdjsonImportHttpTests(Factory factory)
    {
        _factory = factory;
        _client = factory.Client;
    }

    [Fact]
    public async Task EndpointMetadataDeclaresTheImportContractAndIndependentAdmissionPolicy()
    {
        await using var factory = new Factory();
        _ = factory.Client;
        var endpoint = ImportEndpoint(factory);
        Assert.Equal("ImportNdjson", endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName);
        Assert.Equal("ndjson-import", endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()!.PolicyName);
        Assert.Equal(ClickHouseWorkload.Write, endpoint.Metadata.GetMetadata<ClickHouseWorkloadMetadata>()!.Workload);
        var response = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>());
        Assert.Equal(202, response.StatusCode);
        Assert.Equal(typeof(NdjsonImportResponse), response.Type);
    }

    [Fact]
    public void RepeatedRateLimitConfigurationKeepsIndependentAdmissionInstances()
    {
        var factory = _factory.Services.GetRequiredService<IOptionsFactory<RateLimiterOptions>>();
        var first = factory.Create(Options.DefaultName);
        var second = factory.Create(Options.DefaultName);
        try
        {
            Assert.NotSame(first.GlobalLimiter, second.GlobalLimiter);
            Assert.Equal(429, first.RejectionStatusCode);
            Assert.Equal(429, second.RejectionStatusCode);
            var context = new DefaultHttpContext();
            context.Request.ContentLength = 0;
            context.SetEndpoint(ImportEndpoint(_factory));
            using var left = first.GlobalLimiter!.AttemptAcquire(context);
            using var right = second.GlobalLimiter!.AttemptAcquire(context);
            Assert.True(left.IsAcquired);
            Assert.True(right.IsAcquired);
        }
        finally
        {
            first.GlobalLimiter?.Dispose();
            second.GlobalLimiter?.Dispose();
        }
    }

    [Fact]
    public async Task AcceptedImportsApplyTheirConfiguredLimitToTheHostFeature()
    {
        using var scope = _factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.QueryString = new QueryString("?k=server-key");
        context.Request.ContentType = "application/x-ndjson";
        context.Request.Body = new MemoryStream();
        context.Response.Body = new MemoryStream();
        var feature = new MutableBodyLimit();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        await ImportEndpoint(_factory).RequestDelegate!(context);
        Assert.Equal(202, context.Response.StatusCode);
        Assert.Equal(4096, feature.MaxRequestBodySize);
    }

    private static RouteEndpoint ImportEndpoint(Factory factory) => Assert.Single(
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
        endpoint => endpoint.RoutePattern.RawText == Route);

    private sealed class MutableBodyLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    [Theory]
    [InlineData(null, null, 401)]
    [InlineData("unknown", null, 401)]
    [InlineData("public-key", null, 403)]
    [InlineData("unpinned-key", null, 403)]
    [InlineData("unit-key-1", null, 403)]
    [InlineData("server-key", "unknown", 401)]
    [InlineData("unknown", "server-key", 202)]
    [InlineData(null, "server-key", 202)]
    public async Task OnlyPinnedServerKeysMayImportAndHeadersKeepPrecedence(string? query, string? header, int status)
    {
        long before = _factory.Wal.UncommittedRecordCount;
        using var request = Request("", query, header);
        using var response = await _client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(before, _factory.Wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData(null, 415)]
    [InlineData("application/json", 415)]
    [InlineData("application/x-ndjson; charset=utf-16", 415)]
    [InlineData("application/x-ndjson", 202)]
    [InlineData("APPLICATION/X-NDJSON; charset=UTF-8", 202)]
    [InlineData("application/x-ndjson; charset=\"utf-8\"", 202)]
    public async Task ContentTypesAndEmptyFilesHaveAnExplicitContract(string? contentType, int status)
    {
        using var request = Request("");
        request.Content!.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);
        using var response = await _client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 202)
        {
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(0, result.RootElement.GetProperty("acceptedRecords").GetInt32());
            Assert.Equal(0, result.RootElement.GetProperty("nextByteOffset").GetInt64());
            Assert.Equal(1, result.RootElement.GetProperty("nextLine").GetInt32());
            Assert.True(result.RootElement.GetProperty("complete").GetBoolean());
            Assert.False(result.RootElement.TryGetProperty("error", out _));
            Assert.False(result.RootElement.TryGetProperty("statusCode", out _));
        }
    }

    [Theory]
    [InlineData("malformed", "invalid_json", 400)]
    [InlineData("project", "project_not_permitted_for_key", 400)]
    [InlineData("event", "event_type_not_permitted_for_key", 400)]
    [InlineData("identifier", "missing_tenant_id", 400)]
    [InlineData("line", "line_too_large", 413)]
    [InlineData("records", "record_limit", 400)]
    public async Task RejectionReceiptsIdentifyOnlyTheDurablyAcceptedPrefix(string scenario, string error, int status)
    {
        string first = NdjsonRecordReaderTests.Event(extra: ",\"dimensions\":{\"unknown_tag\":\"x\"},\"measures\":{\"unknown_amount\":\"9\"},\"_walRecordId\":\"caller\"");
        string second = scenario switch
        {
            "malformed" => "{",
            "project" => NdjsonRecordReaderTests.Event("other"),
            "event" => NdjsonRecordReaderTests.Event().Replace("action", "forbidden"),
            "identifier" => NdjsonRecordReaderTests.Event().Replace("\"tenantId\":\"t\"", "\"tenantId\":\"\""),
            "line" => new string(' ', 1025),
            _ => NdjsonRecordReaderTests.Event()
        };
        string prefix = first + "\r\n" + (scenario == "records" ? second + "\n" : "");
        string body = scenario == "records" ? prefix + second : prefix + second + "\n" + NdjsonRecordReaderTests.Event();
        long before = _factory.Wal.UncommittedRecordCount;
        using var request = Request(body);
        using var response = await _client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        int accepted = scenario == "records" ? 2 : 1;
        Assert.Equal(accepted, result.RootElement.GetProperty("acceptedRecords").GetInt32());
        Assert.Equal(Encoding.UTF8.GetByteCount(prefix), result.RootElement.GetProperty("nextByteOffset").GetInt64());
        Assert.Equal(accepted + 1, result.RootElement.GetProperty("nextLine").GetInt32());
        Assert.False(result.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(error, result.RootElement.GetProperty("error").GetString());
        Assert.Equal(before + accepted, _factory.Wal.UncommittedRecordCount);
        var persisted = (await File.ReadAllLinesAsync(_factory.WalPath)).Skip((int)before).Select(line => JsonSerializer.Deserialize(line, TelemetryAotContext.Default.GlobalTelemetryPayload)!).ToArray();
        Assert.Equal(accepted, persisted.Length);
        Assert.All(persisted, payload =>
        {
            Assert.Equal("p", payload.ProjectId);
            Assert.Equal("action", payload.EventType);
            Assert.NotEqual(Guid.Empty, payload.WalRecordId);
            Assert.Equal(Guid.Parse(NdjsonRecordReaderTests.Identity), payload.EventId);
        });
        Assert.Empty(persisted[0].Dimensions!);
        Assert.Empty(persisted[0].Measures!);
    }

    [Fact]
    public async Task KnownBodyOverflowIsRejectedBeforePersistence()
    {
        long before = _factory.Wal.UncommittedRecordCount;
        using var request = Request(new string('x', 4097));
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, result.RootElement.GetProperty("acceptedRecords").GetInt32());
        Assert.Equal("body_too_large", result.RootElement.GetProperty("error").GetString());
        Assert.Equal(before, _factory.Wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("custom")]
    public async Task BothExecutionPathsReturnAReceiptAfterRealFilePersistence(string path)
    {
        await using var factory = new Factory(path: path, kestrel: true);
        using var request = Request(NdjsonRecordReaderTests.Event() + "\n" + NdjsonRecordReaderTests.Event());
        using var response = await factory.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, result.RootElement.GetProperty("acceptedRecords").GetInt32());
        Assert.Equal(request.Content!.Headers.ContentLength, result.RootElement.GetProperty("nextByteOffset").GetInt64());
        Assert.True(result.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(3, result.RootElement.GetProperty("nextLine").GetInt32());
        Assert.Equal(2, (await File.ReadAllLinesAsync(factory.WalPath)).Length);
    }

    [Fact]
    public async Task DisabledImportsExposeNoRouteOrFeatureServices()
    {
        await using var factory = new Factory(enabled: false);
        using var request = Request(NdjsonRecordReaderTests.Event());
        using var response = await factory.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(factory.Services.GetService<Nealytics.Engine.Features.ImportNdjson.NdjsonImporter>());
        Assert.Null(factory.Services.GetService<Nealytics.Engine.Features.ImportNdjson.NdjsonImportLimits>());
    }

    [Fact]
    public async Task ABlockedImportCannotConsumeEveryIngestionPermitAndItsOwnPermitIsReleased()
    {
        await using var factory = new Factory(kestrel: true);
        HttpClient client = factory.Client;
        long before = factory.Wal.UncommittedRecordCount;
        using var content = new HeldContent(NdjsonRecordReaderTests.Event() + "\n");
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-ndjson");
        using var first = new HttpRequestMessage(HttpMethod.Post, Route + "?k=server-key") { Content = content };
        Task<HttpResponseMessage> pending = client.SendAsync(first);
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
            while (factory.Wal.UncommittedRecordCount == before) await Task.Delay(1, deadline.Token);
            using var second = Request("");
            using var refused = await client.SendAsync(second);
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            using var track = await client.PostAsync("/api/v1/telemetry/track?k=server-key",
                new StringContent(NdjsonRecordReaderTests.Event(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, track.StatusCode);
        }
        finally
        {
            content.Release.TrySetResult();
        }
        using var completed = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Accepted, completed.StatusCode);
        using var after = Request("");
        using var available = await client.SendAsync(after);
        Assert.Equal(HttpStatusCode.Accepted, available.StatusCode);
    }

    [Theory]
    [InlineData("managed", "gzip")]
    [InlineData("custom", "gzip")]
    [InlineData("managed", "br")]
    [InlineData("custom", "br")]
    [InlineData("managed", "deflate")]
    [InlineData("custom", "deflate")]
    public async Task CompressedFilesKeepTheDecodedBodyBudget(string path, string encoding)
    {
        await using var factory = new Factory(path: path, records: 1000);
        string body = string.Concat(Enumerable.Repeat(NdjsonRecordReaderTests.Event() + "\n", 100));
        using MemoryStream compressed = new();
        using (Stream compressor = encoding switch
        {
            "gzip" => new GZipStream(compressed, CompressionLevel.Fastest, true),
            "br" => new BrotliStream(compressed, CompressionLevel.Fastest, true),
            _ => new ZLibStream(compressed, CompressionLevel.Fastest, true)
        })
        {
            await compressor.WriteAsync(Encoding.UTF8.GetBytes(body));
        }
        using var request = Request("");
        request.Content = new ByteArrayContent(compressed.ToArray());
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-ndjson");
        request.Content.Headers.ContentEncoding.Add(encoding);
        using var response = await factory.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("body_too_large", receipt.RootElement.GetProperty("error").GetString());
        Assert.Equal(factory.Wal.UncommittedRecordCount, receipt.RootElement.GetProperty("acceptedRecords").GetInt64());
    }

    [Theory]
    [InlineData("alpha", "", "region")]
    [InlineData("beta", "150", "category")]
    public async Task ImportsUseOnlyThePinnedProjectsLogicalSchemaAndBounds(string project, string value, string dimension)
    {
        await using var factory = new ProjectContextHttpTests.Factory(customize: options => options.EnableNdjsonImport = true);
        using var client = factory.CreateClient();
        string body = NdjsonRecordReaderTests.Event(project, ",\"dimensions\":{\"region\":\"east\",\"category\":\"selected\"},\"measures\":{\"amount\":\"150\"}");
        using var request = Request(body, project + "-key");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = await factory.Services.GetRequiredService<TelemetryChannelBroker>().Reader.ReadAsync();
        Assert.Equal(project, payload.ProjectId);
        Assert.Equal(dimension, Assert.Single(payload.Dimensions!).Key);
        Assert.Equal(value, payload.Measures!["amount"]);
        using var crossed = Request(NdjsonRecordReaderTests.Event(project == "alpha" ? "beta" : "alpha"), project + "-key");
        using var denied = await client.SendAsync(crossed);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.False(factory.Services.GetRequiredService<TelemetryChannelBroker>().Reader.TryRead(out _));
    }

    private sealed class HeldContent(string prefix) : HttpContent
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(prefix));
            await stream.FlushAsync();
            await Release.Task;
        }
    }

    private static HttpRequestMessage Request(string body, string? query = "server-key", string? header = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route + (query is null ? "" : "?k=" + query))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson")
        };
        if (header is not null) request.Headers.Add("X-Project-Key", header);
        return request;
    }

    public sealed class Factory : NoDatabaseWebFactory
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-import-http-" + Guid.NewGuid().ToString("N"));
        private readonly string _path;
        private readonly string _schema;
        private readonly string? _previous;
        private HttpClient? _client;
        public Factory() : this("managed", true, false) { }
        internal Factory(string path = "managed", bool enabled = true, bool kestrel = false, int records = 2)
        {
            _path = path;
            _schema = _directory + ".json";
            var options = new TelemetryEngineOptions
            {
                EnableNdjsonImport = enabled,
                ImportMaxLineBytes = 1024,
                ImportMaxRecords = records,
                ImportMaxConcurrency = 1,
                MaxRequestBodyBytes = 4096,
                IngestionKeys =
                [
                    new() { Key = "server-key", Scope = "server", ProjectId = "p", EventTypes = "action" },
                    new() { Key = "public-key", Scope = "public", ProjectId = "p" },
                    new() { Key = "unpinned-key", Scope = "server" }
                ]
            };
            File.WriteAllText(_schema, JsonSerializer.Serialize(new Dictionary<string, TelemetryEngineOptions> { ["TelemetryEngine"] = options }));
            _previous = Environment.GetEnvironmentVariable("TelemetryEngine__SchemaFile");
            Environment.SetEnvironmentVariable("TelemetryEngine__SchemaFile", _schema);
            ClientOptions.AllowAutoRedirect = false;
            if (kestrel) UseKestrel(0);
        }
        public HttpClient Client => _client ??= CreateClient();
        public string WalPath => Path.Combine(_directory, "telemetry_wal.log");
        public WriteAheadLogger Wal => Services.GetRequiredService<WriteAheadLogger>();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService)
                    && (service.ImplementationType == typeof(TelemetryBatchProcessor) || service.ImplementationType == typeof(ClickHouseSchemaMigrator))).ToArray())
                {
                    services.Remove(service);
                }
                services.PostConfigure<TelemetryEngineOptions>(options =>
                {
                    options.WriteAheadLogDirectory = _directory;
                    options.IngestionExecutionPath = _path;
                    options.WalIoBackend = Environment.GetEnvironmentVariable("NEALYTICS_TEST_WAL_BACKEND") ?? "managed";
                });
            });
        }
        public override async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            await base.DisposeAsync();
            Environment.SetEnvironmentVariable("TelemetryEngine__SchemaFile", _previous);
            File.Delete(_schema);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}

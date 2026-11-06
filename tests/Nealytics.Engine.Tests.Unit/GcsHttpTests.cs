using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Features.GcsImport;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using static Nealytics.Engine.Tests.Unit.GcsTestSupport;
using Handler = Nealytics.Engine.Tests.Unit.S3TransportTestSupport.Handler;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Project context hosts")]
public sealed class GcsHttpTests
{
    private const string Route = "/api/v1/telemetry/import/gcs/source";

    [Fact]
    public async Task EnabledSourcesResolveTheProductionSingletonDependencyGraphWithoutAnInjectedTransport()
    {
        await using var host = new NdjsonImportHttpTests.Factory(configure: options =>
        {
            options.EnableImportJobs = true;
            options.GcsSources = [GcsTestSupport.Options()];
        });
        var client = host.Services.GetRequiredService<GcsObjectClient>();
        Assert.Same(client, host.Services.GetRequiredService<GcsObjectClient>());
        Assert.NotNull(host.Services.GetRequiredService<GcsImportCoordinator>());
        Assert.Equal(1, host.Services.GetRequiredService<GcsSources>().Count);
    }

    [Fact]
    public async Task ControlRequestsRequireJsonAndAcceptQuotedUtf8RegardlessOfCase()
    {
        await using var host = new Host();
        foreach (string route in new[] { Route + "/inspect", ChunkRoute(Guid.NewGuid(), Guid.NewGuid(), 0) })
        {
            using var missing = await host.Client.PostAsync(route, new ByteArrayContent("{}"u8.ToArray()));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, missing.StatusCode);
        }
        Assert.Empty(host.Transport.Requests);
        using var request = new HttpRequestMessage(HttpMethod.Post, Route + "/inspect")
        {
            Content = new StringContent("{\"key\":\"events/file.ndjson\"}")
        };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("APPLICATION/JSON; charset=\"UTF-8\"");
        using var accepted = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteErrorsUseSafeTransportResponsesOnBothRoutes(bool append)
    {
        await using var host = new Host(send: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("private remote error payload") }));
        using var response = append
            ? await host.Client.PostAsJsonAsync(ChunkRoute(Guid.NewGuid(), Guid.NewGuid(), 0), Snapshot(host))
            : await host.Client.PostAsJsonAsync(Route + "/inspect", new GcsObjectRequest("events/file.ndjson", null));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("{\"error\":\"source_object_not_found\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Factory.Services.GetRequiredService<WriteAheadLogger>().UncommittedRecordCount);
    }

    [Fact]
    public async Task AJournalFailureDoesNotInventAReceiptOrDisableOrdinaryIngestion()
    {
        await using var host = new Host();
        Guid job = Guid.NewGuid();
        string temporary = Path.Combine(Path.GetDirectoryName(host.Base.WalPath)!, "import-jobs", job.ToString("N") + ".tmp");
        File.WriteAllText(temporary, "injected failure");
        using var response = await host.Client.PostAsJsonAsync(ChunkRoute(job, Guid.NewGuid(), 0), Snapshot(host));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"job_storage_unavailable\"}", await response.Content.ReadAsStringAsync());
        using var status = await host.Client.GetAsync("/api/v1/telemetry/import/jobs/" + job);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status.StatusCode);
        using var track = await host.Client.PostAsync("/api/v1/telemetry/track",
            new StringContent(NdjsonRecordReaderTests.Event(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, track.StatusCode);
        Assert.Equal(1, host.Factory.Services.GetRequiredService<WriteAheadLogger>().UncommittedRecordCount);
        await Inspect(host);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StreamedAndDecodedControlBodiesHaveTheSameBoundBeforeAnySourceRequest(bool append, bool compressed)
    {
        await using var host = new Host();
        byte[] bytes = Encoding.UTF8.GetBytes(new string(' ', 4097));
        if (compressed)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) await gzip.WriteAsync(bytes);
            bytes = output.ToArray();
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, append ? ChunkRoute(Guid.NewGuid(), Guid.NewGuid(), 0) : Route + "/inspect")
        {
            Content = new StreamedContent(bytes)
        };
        request.Content.Headers.ContentType = new("application/json");
        if (compressed) request.Content.Headers.ContentEncoding.Add("gzip");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("{\"error\":\"control_body_too_large\"}", await response.Content.ReadAsStringAsync());
        Assert.Empty(host.Transport.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RouteMetadataAndBodyReadFailuresPreserveThePublicControlContract(bool append, bool oversized)
    {
        await using var host = new Host();
        string pattern = append ? "/api/v1/telemetry/import/gcs/{source}/jobs/{jobId:guid}/chunks/{chunkId:guid}"
            : "/api/v1/telemetry/import/gcs/{source}/inspect";
        var endpoint = Assert.Single(host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            route => route.RoutePattern.RawText == pattern);
        Assert.Equal(append ? "ImportGcsChunk" : "InspectGcsObject", endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName);
        Assert.Equal("ndjson-import", endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()!.PolicyName);
        Assert.Equal(ClickHouseWorkload.Write, endpoint.Metadata.GetMetadata<ClickHouseWorkloadMetadata>()!.Workload);
        var produces = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>());
        Assert.Equal(append ? 202 : 200, produces.StatusCode);
        Assert.Equal(append ? typeof(ImportJobResponse) : typeof(GcsObjectSnapshot), produces.Type);
        using var scope = host.Factory.Services.CreateScope();
        using var body = new FailingBody();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.RouteValues["source"] = "source";
        context.Request.RouteValues["jobId"] = Guid.NewGuid().ToString();
        context.Request.RouteValues["chunkId"] = Guid.NewGuid().ToString();
        context.Request.QueryString = new("?offset=0");
        context.Request.Headers["X-Project-Key"] = "server-key";
        context.Request.ContentType = "application/json";
        if (oversized) context.Request.ContentLength = 4097;
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();
        var limit = new BodyLimit();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(limit);
        await endpoint.RequestDelegate!(context);
        Assert.Equal(oversized ? 413 : 400, context.Response.StatusCode);
        Assert.Equal(oversized ? null : (long?)4096, limit.MaxRequestBodySize);
        Assert.Equal(oversized ? 0 : 1, body.Reads);
        context.Response.Body.Position = 0;
        Assert.Equal(oversized ? "{\"error\":\"control_body_too_large\"}" : "{\"error\":\"control_body_unavailable\"}",
            await new StreamReader(context.Response.Body).ReadToEndAsync());
        Assert.Empty(host.Transport.Requests);
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("custom")]
    public async Task RealKestrelImportsTwoCheckpointedChunksAndConfirmedRetriesDoNotAppendAgain(string execution)
    {
        await using var host = new Host(execution: execution, kestrel: true);
        var snapshot = await Inspect(host);
        Assert.Equal("events/file.ndjson", snapshot.Key);
        Assert.Equal(17, snapshot.Generation);
        Assert.Equal(3, snapshot.Metageneration);
        Assert.Equal("\"tag\"", snapshot.ETag);
        Assert.Equal(host.Body.Length, snapshot.Bytes);
        Guid job = Guid.NewGuid(); Guid firstId = Guid.NewGuid();
        string firstRoute = ChunkRoute(job, firstId, 0);
        string receipt;
        using (var first = await host.Client.PostAsJsonAsync(firstRoute, snapshot))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            receipt = await first.Content.ReadAsStringAsync();
        }
        using var ready = JsonDocument.Parse(receipt);
        long next = ready.RootElement.GetProperty("nextByteOffset").GetInt64();
        Assert.Equal("ready", ready.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, ready.RootElement.GetProperty("acceptedRecords").GetInt64());
        Assert.Single(await File.ReadAllLinesAsync(host.Base.WalPath));
        using (var retry = await host.Client.PostAsJsonAsync(firstRoute, snapshot))
        {
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
            Assert.Equal(receipt, await retry.Content.ReadAsStringAsync());
        }
        Assert.Single(await File.ReadAllLinesAsync(host.Base.WalPath));
        string finalRoute = ChunkRoute(job, Guid.NewGuid(), next);
        using var final = await host.Client.PostAsJsonAsync(finalRoute, snapshot);
        Assert.Equal(HttpStatusCode.Accepted, final.StatusCode);
        string completed = await final.Content.ReadAsStringAsync();
        using var complete = JsonDocument.Parse(completed);
        Assert.Equal("complete", complete.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, complete.RootElement.GetProperty("acceptedRecords").GetInt64());
        Assert.Equal(snapshot.Bytes, complete.RootElement.GetProperty("nextByteOffset").GetInt64());
        using var repeated = await host.Client.PostAsJsonAsync(finalRoute, snapshot);
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(completed, await repeated.Content.ReadAsStringAsync());
        using var status = await host.Client.GetAsync("/api/v1/telemetry/import/jobs/" + job);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(completed, await status.Content.ReadAsStringAsync());
        Assert.Equal(2, (await File.ReadAllLinesAsync(host.Base.WalPath)).Length);
        var gets = host.Transport.Requests.Where(request => request.Method == HttpMethod.Get).ToArray();
        Assert.Equal(4, gets.Length);
        Assert.Equal("bytes=0-" + (next - 1), gets[1].Headers.Range!.ToString());
        Assert.All(gets, request => Assert.Equal("\"tag\"", Assert.Single(request.Headers.IfMatch).Tag));
    }

    [Theory]
    [InlineData(null, null, 401)]
    [InlineData("unknown", null, 401)]
    [InlineData("public-key", null, 403)]
    [InlineData("unpinned-key", null, 403)]
    [InlineData("unit-key-1", null, 403)]
    [InlineData("server-key", "unknown", 401)]
    public async Task BothRoutesAuthenticateBeforeParsingOrSendingAnySourceRequest(string? query, string? header, int status)
    {
        await using var host = new Host();
        host.Client.DefaultRequestHeaders.Remove("X-Project-Key");
        foreach (string route in new[] { Route + "/inspect", ChunkRoute(Guid.NewGuid(), Guid.NewGuid(), 0) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route + (route.Contains('?') ? "&" : "?") + (query is null ? "" : "k=" + query))
            {
                Content = new StringContent("{", Encoding.UTF8, "application/json")
            };
            if (header is not null) request.Headers.Add("X-Project-Key", header);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(status, (int)response.StatusCode);
        }
        Assert.Empty(host.Transport.Requests);
        Assert.Equal(0, host.Factory.Services.GetRequiredService<WriteAheadLogger>().UncommittedRecordCount);
    }

    [Fact]
    public async Task HeaderCredentialsKeepPrecedenceAndSourceLookupIsOrdinalAndProjectScoped()
    {
        await using var host = new Host();
        using var accepted = await host.Client.PostAsJsonAsync(Route + "/inspect?k=unknown", new GcsObjectRequest("events/file.ndjson", null));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        foreach (string source in new[] { "missing", "Source" })
        {
            using var response = await host.Client.PostAsync(Route.Replace("/source", "/" + source) + "/inspect", new StringContent("{"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("{\"error\":\"source_not_found\"}", await response.Content.ReadAsStringAsync());
        }
        await using var foreign = new Host(configure: options => options.GcsSources[0].ProjectId = "other");
        using var denied = await foreign.Client.PostAsJsonAsync(Route + "/inspect", new GcsObjectRequest("events/file.ndjson", null));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal("{\"error\":\"source_not_found\"}", await denied.Content.ReadAsStringAsync());
        Assert.Empty(foreign.Transport.Requests);
    }

    [Theory]
    [InlineData("null", "application/json", 400, "invalid_object_request")]
    [InlineData("{", "application/json", 400, "invalid_object_request")]
    [InlineData("{}", "application/json", 400, "invalid_object_request")]
    [InlineData("{}", "text/plain", 415, "unsupported_content_type")]
    [InlineData("{}", "application/json; charset=utf-16", 415, "unsupported_content_type")]
    public async Task InvalidControlRequestsHaveSafeErrorsAndNoRemoteSideEffects(string body, string contentType, int status, string error)
    {
        await using var host = new Host();
        foreach (string route in new[] { Route + "/inspect", ChunkRoute(Guid.NewGuid(), Guid.NewGuid(), 0) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent(body) };
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(status, (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(error, json.RootElement.GetProperty("error").GetString());
        }
        Assert.Empty(host.Transport.Requests);
    }

    [Fact]
    public async Task ControlBodyBudgetsAndInvalidOffsetsFailBeforeFetching()
    {
        await using var host = new Host();
        using var tooLarge = await host.Client.PostAsync(Route + "/inspect", new StringContent(new string(' ', 4097), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        string route = ChunkRoute(Guid.NewGuid(), Guid.NewGuid(), 0);
        foreach (string query in new[] { "", "-1", "+1", "9223372036854775808", "0&offset=0" })
        {
            using var response = await host.Client.PostAsJsonAsync(route.Replace("offset=0", "offset=" + query), new { });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("{\"error\":\"invalid_object_request\"}", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(host.Transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedObjectsStillUseTheExistingMappingAndProjectPolicy(bool mapped)
    {
        string body = mapped ? SourceRecordMappingTests.Event() : NdjsonRecordReaderTests.Event("other");
        await using var host = new Host(body: body, configure: options =>
        {
            if (mapped) options.NdjsonSourceMappings["p"] = SourceRecordMappingTests.Options();
        });
        var snapshot = await Inspect(host);
        using var response = await host.Client.PostAsJsonAsync(ChunkRoute(Guid.NewGuid(), Guid.NewGuid(), 0), snapshot);
        Assert.Equal(mapped ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(mapped ? 1 : 0, host.Factory.Services.GetRequiredService<WriteAheadLogger>().UncommittedRecordCount);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(mapped ? "complete" : "rejected", json.RootElement.GetProperty("state").GetString());
        if (!mapped) Assert.Equal("project_not_permitted_for_key", json.RootElement.GetProperty("receipt").GetProperty("error").GetString());
    }

    [Fact]
    public async Task DisabledSourcesExposeNoRoutesOrAdapterServices()
    {
        await using var host = new NdjsonImportHttpTests.Factory(configure: options => options.EnableImportJobs = true);
        using var response = await host.Client.PostAsJsonAsync(Route + "/inspect?k=server-key", new { key = "file" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(host.Services.GetService<GcsSources>());
        Assert.Null(host.Services.GetService<GcsObjectClient>());
        Assert.Null(host.Services.GetService<GcsImportCoordinator>());
    }

    private static string ChunkRoute(Guid job, Guid chunk, long offset) => Route + "/jobs/" + job + "/chunks/" + chunk + "?offset=" + offset;

    private static GcsObjectSnapshot Snapshot(Host host)
    {
        var source = host.Factory.Services.GetRequiredService<GcsSources>().Find("source", "p")!;
        return GcsTestSupport.Snapshot(source, host.Body.Length, "events/file.ndjson");
    }

    private sealed class StreamedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
    private sealed class FailingBody : MemoryStream
    {
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromException<int>(new IOException("private body detail"));
        }
    }
    private sealed class BodyLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    private static async Task<GcsObjectSnapshot> Inspect(Host host)
    {
        using var response = await host.Client.PostAsJsonAsync(Route + "/inspect", new GcsObjectRequest("events/file.ndjson", null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), TelemetryAotContext.Default.GcsObjectSnapshot)!;
    }

    private sealed class Host : IAsyncDisposable
    {
        public NdjsonImportHttpTests.Factory Base { get; }
        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient Client { get; }
        public Handler Transport { get; }
        public byte[] Body { get; }
        public Host(string execution = "managed", bool kestrel = false, string? body = null, Action<TelemetryEngineOptions>? configure = null,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
        {
            Body = Encoding.UTF8.GetBytes(body ?? NdjsonRecordReaderTests.Event() + "\n" + NdjsonRecordReaderTests.Event());
            Base = new(path: execution, configure: options =>
            {
                options.EnableImportJobs = true;
                options.ImportMaxRecords = 1;
                var source = GcsTestSupport.Options(); source.Prefix = "events/";
                options.GcsSources = [source];
                configure?.Invoke(options);
            });
            Transport = send is not null ? new(send) : new(request =>
            {
                if (request.Method == HttpMethod.Head)
                {
                    return Metadata(Body.Length);
                }
                var range = Assert.Single(request.Headers.Range!.Ranges);
                long from = range.From!.Value; int count = (int)(range.To!.Value - from + 1);
                return Response(Encoding.UTF8.GetString(Body.AsSpan((int)from, count)), Body.Length, from);
            });
            Factory = Base.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<GcsObjectClient>();
                services.AddSingleton(provider => new GcsObjectClient(provider.GetRequiredService<ImportJobLimits>(),
                    provider.GetRequiredService<NdjsonImportLimits>(), Transport));
            }));
            if (kestrel) Factory.UseKestrel(0);
            Client = Factory.CreateClient();
            Client.DefaultRequestHeaders.Add("X-Project-Key", "server-key");
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            await Base.DisposeAsync();
        }
    }
}

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Project context hosts")]
public sealed class ImportJobHttpTests : IClassFixture<ImportJobHttpTests.Fixture>
{
    private readonly NdjsonImportHttpTests.Factory _factory;
    public ImportJobHttpTests(Fixture fixture) => _factory = fixture.Host;
    private static string Route(Guid job) => "/api/v1/telemetry/import/jobs/" + job;

    [Theory]
    [InlineData(null, null, 401)]
    [InlineData("unknown", null, 401)]
    [InlineData("public-key", null, 403)]
    [InlineData("unpinned-key", null, 403)]
    [InlineData("unit-key-1", null, 403)]
    [InlineData("server-key", "unknown", 401)]
    [InlineData("unknown", "server-key", 202)]
    [InlineData(null, "server-key", 202)]
    public async Task BothRoutesRequirePinnedServerCredentialsAndPreserveHeaderPrecedence(string? query, string? header, int expected)
    {
        Guid job = Guid.NewGuid();
        long before = _factory.Wal.UncommittedRecordCount;
        using var request = Request(job, Guid.NewGuid(), "", 0, query: query, header: header);
        using var response = await _factory.Client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        using var status = new HttpRequestMessage(HttpMethod.Get, Route(job) + (query is null ? "" : "?k=" + query));
        if (header is not null) status.Headers.Add("X-Project-Key", header);
        using var read = await _factory.Client.SendAsync(status);
        Assert.Equal(expected == 202 ? 200 : expected, (int)read.StatusCode);
        Assert.Equal(before, _factory.Wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("custom")]
    public async Task RealKestrelPersistsTwoChunksAndRetriesWithoutAnotherWalAppend(string execution)
    {
        await using var factory = new NdjsonImportHttpTests.Factory(path: execution, kestrel: true,
            configure: options => options.EnableImportJobs = true);
        Guid job = Guid.NewGuid();
        Guid chunk = Guid.NewGuid();
        string first = NdjsonRecordReaderTests.Event() + "\r\n";
        string second = NdjsonRecordReaderTests.Event();
        long total = Encoding.UTF8.GetByteCount(first + second);
        string receipt;
        using (var request = Request(job, chunk, first, total))
        using (var response = await factory.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            receipt = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(receipt);
            Assert.Equal("ready", json.RootElement.GetProperty("state").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("acceptedRecords").GetInt64());
            Assert.False(json.RootElement.TryGetProperty("contract", out _));
            Assert.False(json.RootElement.TryGetProperty("projectId", out _));
            Assert.False(json.RootElement.TryGetProperty("chunkHash", out _));
        }
        Assert.Single(await File.ReadAllLinesAsync(factory.WalPath));
        using (var retry = Request(job, chunk, first, total))
        using (var response = await factory.Client.SendAsync(retry))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(receipt, await response.Content.ReadAsStringAsync());
        }
        Assert.Single(await File.ReadAllLinesAsync(factory.WalPath));
        using (var next = Request(job, Guid.NewGuid(), second, total, Encoding.UTF8.GetByteCount(first)))
        using (var response = await factory.Client.SendAsync(next))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("complete", json.RootElement.GetProperty("state").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("acceptedRecords").GetInt64());
            Assert.Equal(total, json.RootElement.GetProperty("nextByteOffset").GetInt64());
        }
        Assert.Equal(2, (await File.ReadAllLinesAsync(factory.WalPath)).Length);
        using var status = await factory.Client.GetAsync(Route(job) + "?k=server-key");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var persisted = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal("complete", persisted.RootElement.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("media", 415, "unsupported_content_type")]
    [InlineData("metadata", 400, "invalid_job_request")]
    [InlineData("known-size", 413, "chunk_too_large")]
    [InlineData("streamed-size", 413, "chunk_too_large")]
    [InlineData("gzip-size", 413, "chunk_too_large")]
    [InlineData("boundary", 400, "invalid_chunk_boundary")]
    public async Task InvalidRequestsNeverCreateJobProgressOrWalRecords(string scenario, int status, string error)
    {
        Guid job = Guid.NewGuid();
        string body = scenario.EndsWith("size", StringComparison.Ordinal) ? new string('x', 4097) : "{}";
        using var request = Request(job, Guid.NewGuid(), body, 9000);
        if (scenario == "media") request.Content!.Headers.ContentType = new("application/json");
        if (scenario == "metadata") request.Headers.Remove("X-Import-Source");
        if (scenario == "streamed-size") request.Content = new StreamedContent(Encoding.UTF8.GetBytes(body));
        if (scenario == "gzip-size")
        {
            using var bytes = new MemoryStream();
            using (var compressor = new GZipStream(bytes, CompressionLevel.Fastest, true))
                await compressor.WriteAsync(Encoding.UTF8.GetBytes(body));
            request.Content = new ByteArrayContent(bytes.ToArray());
            request.Content.Headers.ContentEncoding.Add("gzip");
        }
        if (scenario is "streamed-size" or "gzip-size") request.Content!.Headers.ContentType = new("application/x-ndjson");
        long before = _factory.Wal.UncommittedRecordCount;
        using var response = await _factory.Client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(error, json.RootElement.GetProperty("error").GetString());
        Assert.Equal(before, _factory.Wal.UncommittedRecordCount);
        using var lookup = await _factory.Client.GetAsync(Route(job) + "?k=server-key");
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
    }

    [Fact]
    public async Task ValidationFailurePersistsItsDurablePrefixAndReturnsTheIdenticalRejectedReceipt()
    {
        string body = NdjsonRecordReaderTests.Event() + "\n{\n";
        Guid job = Guid.NewGuid();
        Guid chunk = Guid.NewGuid();
        long before = _factory.Wal.UncommittedRecordCount;
        using var request = Request(job, chunk, body, Encoding.UTF8.GetByteCount(body));
        using var response = await _factory.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string receipt = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(receipt);
        Assert.Equal("rejected", json.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("acceptedRecords").GetInt64());
        Assert.Equal("invalid_json", json.RootElement.GetProperty("receipt").GetProperty("error").GetString());
        using var retry = Request(job, chunk, body, Encoding.UTF8.GetByteCount(body));
        using var repeated = await _factory.Client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Equal(receipt, await repeated.Content.ReadAsStringAsync());
        Assert.Equal(before + 1, _factory.Wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task FaultedJournalMasksStorageDetailsAndLeavesOrdinaryIngestionAvailable()
    {
        await using var factory = new NdjsonImportHttpTests.Factory(configure: options => options.EnableImportJobs = true);
        _ = factory.Client;
        Guid job = Guid.NewGuid();
        string temporary = Path.Combine(Path.GetDirectoryName(factory.WalPath)!, "import-jobs", job.ToString("N") + ".tmp");
        File.WriteAllText(temporary, "injected torn replacement");
        using var request = Request(job, Guid.NewGuid(), "", 0);
        using var response = await factory.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"job_storage_unavailable\"}", await response.Content.ReadAsStringAsync());
        using var status = await factory.Client.GetAsync(Route(job) + "?k=server-key");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status.StatusCode);
        Assert.Equal("{\"error\":\"job_storage_unavailable\"}", await status.Content.ReadAsStringAsync());
        using var track = await factory.Client.PostAsync("/api/v1/telemetry/track?k=server-key",
            new StringContent(NdjsonRecordReaderTests.Event(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, track.StatusCode);
    }

    [Fact]
    public async Task ProjectKeysCannotReadOrAppendAnotherProjectsJob()
    {
        await using var factory = new ProjectContextHttpTests.Factory(customize: options =>
        {
            options.EnableNdjsonImport = true;
            options.EnableImportJobs = true;
        });
        using var client = factory.CreateClient();
        Guid job = Guid.NewGuid();
        Guid chunk = Guid.NewGuid();
        using var first = Request(job, chunk, "", 0, query: "alpha-key");
        using var accepted = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var status = await client.GetAsync(Route(job) + "?k=beta-key");
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        using var crossed = Request(job, chunk, "", 0, query: "beta-key");
        using var denied = await client.SendAsync(crossed);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task DisabledJobsExposeNoRoutesOrServicesAndInvalidDependenciesFailStartup()
    {
        await using (var disabled = new NdjsonImportHttpTests.Factory())
        {
            using var response = await disabled.Client.GetAsync(Route(Guid.NewGuid()) + "?k=server-key");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Null(disabled.Services.GetService<ImportJobCoordinator>());
            Assert.Null(disabled.Services.GetService<ImportJobStore>());
            Assert.Null(disabled.Services.GetService<ImportJobContracts>());
        }
        await using var invalid = new NdjsonImportHttpTests.Factory(enabled: false, configure: options => options.EnableImportJobs = true);
        Assert.Equal("Import jobs require NDJSON import, 1–4096 jobs and a positive body limit.",
            Assert.Throws<InvalidOperationException>(() => invalid.Client).Message);
    }

    [Fact]
    public async Task RouteMetadataAndMutableHostLimitDeclareIndependentImportAdmission()
    {
        await using var host = new NdjsonImportHttpTests.Factory(configure: options => options.EnableImportJobs = true);
        var endpoint = Assert.Single(host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            route => route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "AppendImportChunk");
        var read = Assert.Single(host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            route => route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetImportJob");
        Assert.Equal("/api/v1/telemetry/import/jobs/{jobId:guid}", read.RoutePattern.RawText);
        Assert.Equal(typeof(ImportJobResponse), Assert.Single(read.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()).Type);
        Assert.Equal("ndjson-import", endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()!.PolicyName);
        Assert.Equal(ClickHouseWorkload.Write, endpoint.Metadata.GetMetadata<ClickHouseWorkloadMetadata>()!.Workload);
        var metadata = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>());
        Assert.Equal(202, metadata.StatusCode);
        Assert.Equal(typeof(ImportJobResponse), metadata.Type);
        using var scope = host.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.RouteValues["jobId"] = Guid.NewGuid().ToString();
        context.Request.RouteValues["chunkId"] = Guid.NewGuid().ToString();
        context.Request.QueryString = new("?k=server-key&offset=0");
        context.Request.Headers["X-Import-Source"] = ImportJobTestSupport.Source;
        context.Request.Headers["X-Import-Source-Bytes"] = "0";
        context.Request.ContentType = "application/x-ndjson";
        context.Request.Body = new MemoryStream();
        context.Response.Body = new MemoryStream();
        var limit = new BodyLimit();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(limit);
        await endpoint.RequestDelegate!(context);
        Assert.Equal(202, context.Response.StatusCode);
        Assert.Equal(4096, limit.MaxRequestBodySize);
    }

    private static HttpRequestMessage Request(Guid job, Guid chunk, string body, long total, long offset = 0,
        string? query = "server-key", string? header = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route(job) + "/chunks/" + chunk + "?offset=" + offset
            + (query is null ? "" : "&k=" + query)) { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") };
        request.Headers.Add("X-Import-Source", ImportJobTestSupport.Source);
        request.Headers.Add("X-Import-Source-Bytes", total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (header is not null) request.Headers.Add("X-Project-Key", header);
        return request;
    }

    private sealed class BodyLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }
    private sealed class StreamedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
    public sealed class Fixture : IAsyncLifetime
    {
        public NdjsonImportHttpTests.Factory Host { get; } = new(configure: options => options.EnableImportJobs = true);
        public Task InitializeAsync() => Task.CompletedTask;
        public Task DisposeAsync() => Host.DisposeAsync().AsTask();
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nealytics.Engine.Features.HttpImport;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Infrastructure.Configuration;
using static Nealytics.Engine.Tests.Unit.S3TransportTestSupport;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Project context hosts")]
public sealed class HttpImportEndpointTests
{
    private const string Route = "/api/v1/telemetry/import/http/source";

    [Theory]
    [InlineData("managed", false)]
    [InlineData("custom", false)]
    [InlineData("managed", true)]
    [InlineData("custom", true)]
    public async Task NamedHttpObjectsUseCheckpointedWalAdmissionAndExactConfirmedRetries(string execution, bool kestrel)
    {
        await using var host = new Host(execution: execution, kestrel: kestrel);
        using var inspect = await host.Client.PostAsync(Route + "/inspect", null);
        Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
        var snapshot = (await inspect.Content.ReadFromJsonAsync<HttpObjectSnapshot>())!;
        Assert.Equal(host.Body.Length, snapshot.Bytes); Assert.Equal("\"tag\"", snapshot.ETag);
        string metadata = await inspect.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", metadata); Assert.DoesNotContain("origin.example", metadata);
        Guid job = Guid.NewGuid(); Guid first = Guid.NewGuid();
        string firstRoute = ChunkRoute(job, first, 0);
        string receipt;
        using (var accepted = await host.Client.PostAsJsonAsync(firstRoute, snapshot))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            receipt = await accepted.Content.ReadAsStringAsync();
        }
        using var parsed = JsonDocument.Parse(receipt);
        long next = parsed.RootElement.GetProperty("nextByteOffset").GetInt64();
        Assert.Equal("ready", parsed.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, host.Wal.UncommittedRecordCount);
        using var repeated = await host.Client.PostAsJsonAsync(firstRoute, snapshot);
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(receipt, await repeated.Content.ReadAsStringAsync());
        Assert.Equal(1, host.Wal.UncommittedRecordCount);
        using var finished = await host.Client.PostAsJsonAsync(ChunkRoute(job, Guid.NewGuid(), next), snapshot);
        Assert.Equal(HttpStatusCode.Accepted, finished.StatusCode);
        string completed = await finished.Content.ReadAsStringAsync();
        using var final = JsonDocument.Parse(completed);
        Assert.Equal("complete", final.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, final.RootElement.GetProperty("acceptedRecords").GetInt64());
        Assert.Equal(snapshot.Bytes, final.RootElement.GetProperty("nextByteOffset").GetInt64());
        using var status = await host.Client.GetAsync("/api/v1/telemetry/import/jobs/" + job);
        Assert.Equal(completed, await status.Content.ReadAsStringAsync());
        Assert.Equal(2, host.Wal.UncommittedRecordCount);
        Assert.All(host.Transport.Requests, request => Assert.Equal(Host.Url, request.RequestUri!.AbsoluteUri));
        Assert.All(host.Transport.Requests, request => Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString()));
    }

    [Theory]
    [InlineData(null, null, 401)]
    [InlineData("unknown", null, 401)]
    [InlineData("public-key", null, 403)]
    [InlineData("unpinned-key", null, 403)]
    [InlineData("unit-key-1", null, 403)]
    [InlineData("server-key", "unknown", 401)]
    public async Task BothRoutesAuthenticateBeforeSourceLookupOrBodyParsing(string? query, string? key, int expected)
    {
        await using var host = new Host();
        host.Client.DefaultRequestHeaders.Remove("X-Project-Key");
        foreach (string route in new[] { Route + "/inspect", ChunkRoute(Guid.NewGuid(), Guid.NewGuid(), 0) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route + (query is null ? "" : (route.Contains('?') ? "&" : "?") + "k=" + query))
                { Content = new StringContent("malformed") };
            if (key is not null) request.Headers.Add("X-Project-Key", key);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(expected, (int)response.StatusCode);
        }
        Assert.Empty(host.Transport.Requests);
    }

    [Fact]
    public async Task InspectionCannotAcceptARequestSuppliedLocatorAndUnknownSourcesAreHidden()
    {
        await using var host = new Host();
        using var rejected = await host.Client.PostAsync(Route + "/inspect", new StringContent("https://other.example/secret"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("{\"error\":\"unexpected_control_body\"}", await rejected.Content.ReadAsStringAsync());
        Assert.Empty(host.Transport.Requests);
        using var missing = await host.Client.PostAsync(Route.Replace("source", "other") + "/inspect", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("{\"error\":\"source_not_found\"}", await missing.Content.ReadAsStringAsync());
        Assert.Empty(host.Transport.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"etag\":\"\\\"tag\\\"\",\"bytes\":3,\"sourceId\":\"forged\"}")]
    public async Task UnverifiableSnapshotBodiesNeverFetchOrCreateJobs(string body)
    {
        await using var host = new Host();
        using var response = await host.Client.PostAsync(ChunkRoute(Guid.NewGuid(), Guid.NewGuid(), 0), new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_object_request\"}", await response.Content.ReadAsStringAsync());
        Assert.Empty(host.Transport.Requests); Assert.Equal(0, host.Wal.UncommittedRecordCount);
        Assert.Equal(0, host.Factory.Services.GetRequiredService<ImportJobStore>().Count);
    }

    [Fact]
    public async Task CheckpointConflictsReturnTheirStructuredErrorWithoutAJobBody()
    {
        await using var host = new Host();
        Guid job = Guid.NewGuid();
        using var accepted = await host.Client.PostAsJsonAsync(ChunkRoute(job, Guid.NewGuid(), 0), Snapshot(host));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var rejected = await host.Client.PostAsJsonAsync(ChunkRoute(job, Guid.NewGuid(), 0), Snapshot(host));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("{\"error\":\"offset_conflict\"}", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(1, host.Wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task EnabledSourcesInstallTheProductionSingletonClientAndCoordinator()
    {
        await using var host = new NdjsonImportHttpTests.Factory(configure: options =>
        {
            options.EnableImportJobs = true; options.HttpSources = [HttpObjectTestSupport.Options()];
        });
        var client = host.Services.GetRequiredService<HttpObjectClient>();
        Assert.Same(client, host.Services.GetRequiredService<HttpObjectClient>());
        Assert.NotNull(host.Services.GetRequiredService<HttpImportCoordinator>());
        Assert.Equal(1, host.Services.GetRequiredService<HttpSources>().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothRoutesDeclareTheirNamesResultContractsRateLimitsAndWorkload(bool append)
    {
        await using var host = new Host();
        string pattern = append ? "/api/v1/telemetry/import/http/{source}/jobs/{jobId:guid}/chunks/{chunkId:guid}"
            : "/api/v1/telemetry/import/http/{source}/inspect";
        var endpoint = Assert.Single(host.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>(), route => route.RoutePattern.RawText == pattern);
        Assert.Equal(append ? "ImportHttpChunk" : "InspectHttpObject", endpoint.Metadata
            .GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()!.EndpointName);
        Assert.Equal("ndjson-import", endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()!.PolicyName);
        Assert.Equal(Nealytics.Engine.Infrastructure.Storage.ClickHouseWorkload.Write,
            endpoint.Metadata.GetMetadata<Nealytics.Engine.Infrastructure.Storage.ClickHouseWorkloadMetadata>()!.Workload);
        var produces = Assert.Single(endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>());
        Assert.Equal(append ? typeof(ImportJobResponse) : typeof(HttpObjectSnapshot), produces.Type);
        Assert.Equal(append ? 202 : 200, produces.StatusCode);
    }

    [Fact]
    public async Task DisabledSourcesDoNotInstallRoutesOrTheirClient()
    {
        await using var host = new NdjsonImportHttpTests.Factory();
        using var response = await host.Client.PostAsync(Route + "/inspect", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(host.Services.GetService<HttpObjectClient>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteErrorsStaySafeOnBothControlRoutes(bool append)
    {
        await using var host = new Host(send: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("private upstream response") }));
        using var response = append
            ? await host.Client.PostAsJsonAsync(ChunkRoute(Guid.NewGuid(), Guid.NewGuid(), 0), Snapshot(host))
            : await host.Client.PostAsync(Route + "/inspect", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("{\"error\":\"source_object_not_found\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("invalid")]
    [InlineData("9223372036854775808")]
    public async Task InvalidOffsetsFailBeforeBodyParsingOrSourceRequests(string offset)
    {
        await using var host = new Host();
        using var response = await host.Client.PostAsync(ChunkRoute(Guid.NewGuid(), Guid.NewGuid(), 0).Replace("offset=0", "offset=" + offset),
            new StringContent("not-json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_object_request\"}", await response.Content.ReadAsStringAsync()); Assert.Empty(host.Transport.Requests);
    }

    [Theory]
    [InlineData("text/plain", 415)]
    [InlineData("application/json; charset=utf-16", 415)]
    [InlineData("application/json", 413)]
    public async Task ControlMediaAndKnownByteBudgetsAreAppliedBeforeAnySourceAccess(string media, int status)
    {
        await using var host = new Host();
        using var content = new StringContent(new string(' ', 4097));
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(media);
        using var response = await host.Client.PostAsync(ChunkRoute(Guid.NewGuid(), Guid.NewGuid(), 0), content);
        Assert.Equal(status, (int)response.StatusCode); Assert.Empty(host.Transport.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StreamingBodyFailuresPreserveControlDiagnosticsAndDoNotFetch(bool append, bool oversized)
    {
        await using var host = new Host();
        using var scope = host.Factory.Services.CreateScope();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers["X-Project-Key"] = "server-key";
        context.Request.ContentType = "application/json";
        context.Request.QueryString = new("?offset=0");
        context.Request.RouteValues["source"] = "source";
        context.Request.RouteValues["jobId"] = Guid.NewGuid().ToString();
        context.Request.RouteValues["chunkId"] = Guid.NewGuid().ToString();
        context.Request.Body = new FailingBody(oversized); context.Response.Body = new MemoryStream();
        string pattern = append ? "/api/v1/telemetry/import/http/{source}/jobs/{jobId:guid}/chunks/{chunkId:guid}"
            : "/api/v1/telemetry/import/http/{source}/inspect";
        var endpoint = Assert.Single(host.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>(), route => route.RoutePattern.RawText == pattern);
        await endpoint.RequestDelegate!(context);
        Assert.Equal(oversized ? 413 : 400, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        string error = oversized ? "control_body_too_large" : "control_body_unavailable";
        Assert.Equal("{\"error\":\"" + error + "\"}", await new StreamReader(context.Response.Body).ReadToEndAsync());
        Assert.Empty(host.Transport.Requests);
    }

    [Fact]
    public async Task JournalFaultsLatchOnlyJobsWhileInspectionAndOrdinaryIngestionRemainAvailable()
    {
        await using var host = new Host();
        Guid job = Guid.NewGuid();
        string temporary = Path.Combine(Path.GetDirectoryName(host.Base.WalPath)!, "import-jobs", job.ToString("N") + ".tmp");
        File.WriteAllText(temporary, "injected fault");
        using var response = await host.Client.PostAsJsonAsync(ChunkRoute(job, Guid.NewGuid(), 0), Snapshot(host));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"job_storage_unavailable\"}", await response.Content.ReadAsStringAsync());
        using var inspect = await host.Client.PostAsync(Route + "/inspect", null);
        Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
        using var track = await host.Client.PostAsync("/api/v1/telemetry/track", new StringContent(NdjsonRecordReaderTests.Event(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, track.StatusCode);
    }

    private static HttpObjectSnapshot Snapshot(Host host) => HttpObjectTestSupport.Snapshot(
        host.Factory.Services.GetRequiredService<HttpSources>().Find("source", "p")!, host.Body.Length);

    private sealed class FailingBody(bool oversized) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(oversized ? new Microsoft.AspNetCore.Http.BadHttpRequestException("private", 413) : new IOException("private"));
    }

    private static string ChunkRoute(Guid job, Guid chunk, long offset) =>
        Route + "/jobs/" + job + "/chunks/" + chunk + "?offset=" + offset;

    internal sealed class Host : IAsyncDisposable
    {
        internal const string Url = "https://origin.example/export?secret=query";
        public string Body { get; } = NdjsonRecordReaderTests.Event() + "\r\n" + NdjsonRecordReaderTests.Event();
        public NdjsonImportHttpTests.Factory Base { get; }
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; }
        public HttpClient Client { get; }
        public Handler Transport { get; }
        public Nealytics.Engine.Infrastructure.Storage.WriteAheadLogger Wal =>
            Factory.Services.GetRequiredService<Nealytics.Engine.Infrastructure.Storage.WriteAheadLogger>();
        internal Host(string execution = "managed", bool kestrel = false,
            Action<TelemetryEngineOptions>? configure = null,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
        {
            Transport = new Handler(send ?? ((request, _) =>
            {
                if (request.Method == HttpMethod.Head)
                {
                    var response = Response(); response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(Body); return Task.FromResult(response);
                }
                var range = Assert.Single(request.Headers.Range!.Ranges);
                int start = (int)range.From!.Value; int count = (int)(range.To!.Value - range.From.Value + 1);
                return Task.FromResult(Response(Body.Substring(start, count), Body.Length, start));
            }));
            Base = new NdjsonImportHttpTests.Factory(path: execution, configure: options =>
            {
                options.EnableImportJobs = true; options.ImportMaxRecords = 1;
                var source = HttpObjectTestSupport.Options(); source.Url = Url; source.Headers["Authorization"] = "Bearer secret";
                options.HttpSources = [source]; configure?.Invoke(options);
            });
            Factory = Base.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<HttpObjectClient>();
                services.AddSingleton(provider => new HttpObjectClient(provider.GetRequiredService<ImportJobLimits>(),
                    provider.GetRequiredService<NdjsonImportLimits>(), Transport));
            }));
            if (kestrel) Factory.UseKestrel(0);
            Client = Factory.CreateClient(); Client.DefaultRequestHeaders.Add("X-Project-Key", "server-key");
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await Factory.DisposeAsync(); await Base.DisposeAsync();
        }
    }
}

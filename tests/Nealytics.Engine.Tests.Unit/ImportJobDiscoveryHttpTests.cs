using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nealytics.Engine.Features.ImportJobs;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Project context hosts")]
public sealed class ImportJobDiscoveryHttpTests : IClassFixture<ImportJobDiscoveryHttpTests.Fixture>
{
    private const string Route = "/api/v1/telemetry/import/jobs";
    private readonly NdjsonImportHttpTests.Factory _host;
    public ImportJobDiscoveryHttpTests(Fixture fixture) => _host = fixture.Host;

    [Theory]
    [InlineData(null, null, 401)]
    [InlineData("unknown", null, 401)]
    [InlineData("public-key", null, 403)]
    [InlineData("unpinned-key", null, 403)]
    [InlineData("unit-key-1", null, 403)]
    [InlineData("server-key", "unknown", 401)]
    [InlineData("unknown", "server-key", 200)]
    [InlineData(null, "server-key", 200)]
    public async Task ListingRequiresPinnedServerAuthorizationWithHeaderPrecedence(string? query, string? header, int expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Route + (query is null ? "" : "?k=" + query));
        if (header is not null) request.Headers.Add("X-Project-Key", header);
        using var response = await _host.Client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(0, _host.Wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData("limit=")]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=1&limit=1")]
    [InlineData("after=")]
    [InlineData("after=00000000-0000-0000-0000-000000000000")]
    [InlineData("after=00000001-0000-4000-8000-000000000001&after=00000001-0000-4000-8000-000000000001")]
    public async Task InvalidPagesReturnFixedDiagnosticsOnlyAfterAuthentication(string query)
    {
        using var denied = await _host.Client.GetAsync(Route + "?" + query);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var response = await _host.Client.GetAsync(Route + "?k=server-key&" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_job_list_request\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProjectPagesExposeOnlyExistingPublicProgressWithoutChangingReceiptsOrWal()
    {
        await using var factory = new ProjectContextHttpTests.Factory(customize: options =>
        {
            options.EnableNdjsonImport = true; options.EnableImportJobs = true;
        });
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ImportJobStore>();
        Guid first = Guid.Parse("00000001-0000-4000-8000-000000000001");
        Guid middle = Guid.Parse("00000002-0000-4000-8000-000000000001");
        Guid last = Guid.Parse("00000003-0000-4000-8000-000000000001");
        foreach (var pair in new[] { (last, "alpha"), (middle, "beta"), (first, "alpha") })
            store.Save(ImportJobTestSupport.Ready(ImportJobTestSupport.Request(job: pair.Item1)) with { ProjectId = pair.Item2 });
        using var response = await client.GetAsync(Route + "?k=alpha-key&limit=1&project=beta");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<ImportJobPage>())!;
        var job = Assert.Single(page.Jobs); Assert.Equal(first, job.JobId); Assert.Equal(first, page.NextAfter);
        using var status = await client.GetAsync(Route + "/" + first + "?k=alpha-key");
        Assert.Equal(job, await status.Content.ReadFromJsonAsync<ImportJobResponse>());
        string json = await response.Content.ReadAsStringAsync();
        foreach (string hidden in new[] { "projectId", "contract", "chunkHash", "alpha", "beta" }) Assert.DoesNotContain(hidden, json);
        using var next = await client.GetAsync(Route + "?k=alpha-key&limit=1&after=" + page.NextAfter);
        var final = (await next.Content.ReadFromJsonAsync<ImportJobPage>())!;
        Assert.Equal(last, Assert.Single(final.Jobs).JobId); Assert.Null(final.NextAfter);
        using var foreignCursor = await client.GetAsync(Route + "?k=alpha-key&after=" + middle);
        Assert.Equal(last, Assert.Single((await foreignCursor.Content.ReadFromJsonAsync<ImportJobPage>())!.Jobs).JobId);
        using var other = await client.GetAsync(Route + "?k=beta-key");
        Assert.Equal(middle, Assert.Single((await other.Content.ReadFromJsonAsync<ImportJobPage>())!.Jobs).JobId);
        Assert.Equal(3, store.Count);
        Assert.Equal("ready", store.Find(first)!.Status);
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("custom")]
    public async Task RealKestrelDiscoversExactlyTheConfirmedImportWithoutAnotherAdmission(string execution)
    {
        await using var host = new NdjsonImportHttpTests.Factory(path: execution, kestrel: true,
            configure: options => options.EnableImportJobs = true);
        Guid id = Guid.NewGuid();
        string body = NdjsonRecordReaderTests.Event();
        using var upload = new HttpRequestMessage(HttpMethod.Post, Route + "/" + id + "/chunks/" + Guid.NewGuid() + "?k=server-key&offset=0")
        { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") };
        upload.Headers.Add("X-Import-Source", ImportJobTestSupport.Source);
        upload.Headers.Add("X-Import-Source-Bytes", Encoding.UTF8.GetByteCount(body).ToString());
        using var accepted = await host.Client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var confirmed = await accepted.Content.ReadFromJsonAsync<ImportJobResponse>();
        using var listed = await host.Client.GetAsync(Route + "?k=server-key");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var page = (await listed.Content.ReadFromJsonAsync<ImportJobPage>())!;
        Assert.Equal(confirmed, Assert.Single(page.Jobs)); Assert.Null(page.NextAfter);
        Assert.Single(await File.ReadAllLinesAsync(host.WalPath));
    }

    [Fact]
    public async Task JournalFailureReturnsNoPartialListOrStorageDetailsAndDisabledJobsHaveNoDiscoveryRoute()
    {
        await using (var host = new NdjsonImportHttpTests.Factory(configure: options => options.EnableImportJobs = true))
        {
            _ = host.Client;
            var store = host.Services.GetRequiredService<ImportJobStore>();
            store.Save(ImportJobTestSupport.Ready()); var state = ImportJobTestSupport.Ready();
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(host.WalPath)!, "import-jobs", state.Id.ToString("N") + ".tmp"), "private");
            Assert.Throws<IOException>(() => store.Save(state));
            using var response = await host.Client.GetAsync(Route + "?k=server-key");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("{\"error\":\"job_storage_unavailable\"}", await response.Content.ReadAsStringAsync());
        }
        await using var disabled = new NdjsonImportHttpTests.Factory();
        using var missing = await disabled.Client.GetAsync(Route + "?k=server-key");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var endpoint = Assert.Single(_host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            route => route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "ListImportJobs");
        Assert.Equal(Route, endpoint.RoutePattern.RawText);
        Assert.Equal(typeof(ImportJobPage), Assert.Single(endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()).Type);
    }

    public sealed class Fixture : IAsyncLifetime
    {
        public NdjsonImportHttpTests.Factory Host { get; } = new(configure: options => options.EnableImportJobs = true);
        public Task InitializeAsync() => Task.CompletedTask;
        public Task DisposeAsync() => Host.DisposeAsync().AsTask();
    }
}

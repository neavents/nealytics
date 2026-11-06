using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nealytics.Engine.Features.KafkaIngestion;
using Nealytics.Engine.Infrastructure.Configuration;
using NSubstitute;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Project context hosts")]
public sealed class KafkaSourceHealthHttpTests : IClassFixture<KafkaSourceHealthHttpTests.Fixture>
{
    private const string Route = "/api/v1/telemetry/sources/kafka/default";
    private readonly ProjectContextHttpTests.Factory _host;
    public KafkaSourceHealthHttpTests(Fixture fixture) => _host = fixture.Host;

    [Theory]
    [InlineData(null, null, 401)]
    [InlineData("unknown", null, 401)]
    [InlineData("alpha-public", null, 403)]
    [InlineData("unpinned-server", null, 403)]
    [InlineData("unit-key-2", null, 403)]
    [InlineData("beta-key", null, 404)]
    [InlineData("alpha-key", "unknown", 401)]
    [InlineData("unknown", "alpha-key", 200)]
    [InlineData(null, "alpha-key", 200)]
    public async Task OnlyTheSourcesPinnedProjectCanReadHealthAndHeaderPrecedenceStaysConsistent(string? query, string? header, int expected)
    {
        using var client = _host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Route + (query is null ? "" : "?k=" + query) + (query is null ? "?project=alpha" : "&project=alpha"));
        if (header is not null) request.Headers.Add("X-Project-Key", header);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task DetachedSnapshotsExposeOnlyFixedLabelsAndDoNotNeedDatabaseOrProviderCalls()
    {
        using var client = _host.CreateClient();
        await KafkaTestSupport.WaitFor(() => _host.Services.GetRequiredService<KafkaSourceHealth>().Snapshot().State == "waiting");
        var health = _host.Services.GetRequiredService<KafkaSourceHealth>();
        health.NativeError(new Error(ErrorCode.Local_Authentication, "private password broker endpoint payload"));
        using var response = await client.GetAsync(Route + "?k=alpha-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = (await response.Content.ReadFromJsonAsync<KafkaSourceHealthResponse>())!;
        Assert.Equal("default", status.Name); Assert.Equal("unavailable", status.State); Assert.Equal("authentication_failed", status.Issue);
        Assert.Equal(DateTimeKind.Utc, status.ChangedAt.Kind);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "changedAt", "issue", "name", "state" }, document.RootElement.EnumerateObject().Select(item => item.Name).Order());
        foreach (string hidden in new[] { "private", "password", "broker", "endpoint", "payload", "topic", "groupId", "alpha", "secret-key" })
            Assert.DoesNotContain(hidden, document.RootElement.GetRawText());
        health.NativeError(new Error(ErrorCode.Local_AllBrokersDown)); health.Revoked();
        Assert.Equal(status, health.Snapshot());
        health.Confirmed();
        using var recovered = await client.GetAsync(Route + "?k=alpha-key");
        var assigned = (await recovered.Content.ReadFromJsonAsync<KafkaSourceHealthResponse>())!;
        Assert.Equal("assigned", assigned.State); Assert.Null(assigned.Issue);
        Assert.Equal("unavailable", status.State);
        using var liveness = await client.GetAsync("/health"); Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(0, _host.Services.GetRequiredService<Nealytics.Engine.Infrastructure.Storage.WriteAheadLogger>().UncommittedRecordCount);
    }

    [Fact]
    public async Task EndpointMetadataAndRawBoundaryRejectServerKeysOutsideTheirCatalog()
    {
        using var client = _host.CreateClient();
        var endpoint = Assert.Single(_host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetKafkaSourceHealth");
        Assert.Equal(Route, endpoint.RoutePattern.RawText);
        Assert.Equal(typeof(KafkaSourceHealthResponse), Assert.Single(endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()).Type);
        using var scope = _host.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers["X-Project-Key"] = "disallowed-server";
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrAbsentKafkaExposesNoHealthRouteOrRuntimeState(bool absent)
    {
        await using var host = new ProjectContextHttpTests.Factory(customize: options =>
        {
            options.Kafka = absent ? null : new KafkaConnectorOptions { Enabled = false };
        });
        using var client = host.CreateClient();
        using var response = await client.GetAsync(Route + "?k=alpha-key");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(host.Services.GetService<KafkaSourceHealth>());
    }

    public sealed class Fixture : IAsyncLifetime
    {
        public ProjectContextHttpTests.Factory Host { get; }
        public Fixture()
        {
            var consumer = Substitute.For<IConsumer<Ignore, byte[]>>();
            consumer.Consume(Arg.Any<CancellationToken>()).Returns(call =>
            {
                var token = call.Arg<CancellationToken>(); token.WaitHandle.WaitOne(); throw new OperationCanceledException(token);
            });
            Host = new(customize: options =>
            {
                options.Kafka = KafkaTestSupport.Options().Kafka;
                options.Kafka!.ProjectKey = "alpha-key";
                options.IngestionKeys.Add(new() { Key = "unpinned-server", Scope = "server" });
                options.IngestionKeys.Add(new() { Key = "disallowed-server", Scope = "server", ProjectId = "missing" });
            }, configure: services =>
            {
                services.RemoveAll<Func<IConsumer<Ignore, byte[]>>>();
                services.AddSingleton<Func<IConsumer<Ignore, byte[]>>>(() => consumer);
            });
        }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task DisposeAsync() => Host.DisposeAsync().AsTask();
    }
}

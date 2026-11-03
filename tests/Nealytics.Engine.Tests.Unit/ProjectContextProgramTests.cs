using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.GetSchema;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Delivery;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Features.UpsertTenantAttributes;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Project context hosts")]
public sealed class ProjectContextProgramTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceLifetimesMatchProjectModeAndPhysicalWritersKeepTheUnion(bool projects)
    {
        await using NoDatabaseWebFactory factory = projects ? new ProjectContextHttpTests.Factory() : new NoDatabaseWebFactory();
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<ProjectCatalog>();
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        object[] a = [first.ServiceProvider.GetRequiredService<IngestGate>(), first.ServiceProvider.GetRequiredService<DimensionSanitizer>(), first.ServiceProvider.GetRequiredService<MeasureSanitizer>(), first.ServiceProvider.GetRequiredService<BeaconBatchIngestor>(), first.ServiceProvider.GetRequiredService<GetColumnPopulationQuery>()];
        object[] b = [second.ServiceProvider.GetRequiredService<IngestGate>(), second.ServiceProvider.GetRequiredService<DimensionSanitizer>(), second.ServiceProvider.GetRequiredService<MeasureSanitizer>(), second.ServiceProvider.GetRequiredService<BeaconBatchIngestor>(), second.ServiceProvider.GetRequiredService<GetColumnPopulationQuery>()];
        for (int index = 0; index < a.Length; index++)
        {
            Assert.Equal(!projects, ReferenceEquals(a[index], b[index]));
        }
        var writer = factory.Services.GetRequiredService<ClickHouseBatchWriter>();
        Assert.Same(writer, first.ServiceProvider.GetRequiredService<IDeliveryBatchWriter>());
        Assert.Same(writer, second.ServiceProvider.GetRequiredService<ClickHouseBatchWriter>());
        Assert.Equal(TelemetryDeliverySchema.Fingerprint(new TelemetryColumnLayout(catalog.Storage.Dimensions, catalog.Storage.Measures)), writer.SchemaFingerprint);
        Assert.IsType<JournaledTelemetryBatchWriter>(factory.Services.GetRequiredService<ITelemetryBatchWriter>());
        Assert.Same(factory.Services.GetRequiredService<TelemetryDeliveryJournal>(), first.ServiceProvider.GetRequiredService<TelemetryDeliveryJournal>());
        Assert.Contains(factory.Services.GetServices<IHostedService>(), service => service is ClickHouseSchemaMigrator);
        if (!projects)
        {
            Assert.Contains(factory.Services.GetServices<IHostedService>(), service => service is TelemetryBatchProcessor);
            using var validated = await client.PostAsync("/api/v1/telemetry/validate?k=unit-key-1", new StringContent("""{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrackAndBeaconNormalizeProjectBoundsBeforeDurablePublication(bool batch)
    {
        await using var factory = new ProjectContextHttpTests.Factory();
        using var client = factory.CreateClient();
        const string payload = """{"projectId":"alpha","tenantId":"t","sessionId":"s","eventType":"e","measures":{"amount":"-1"}}""";
        using var response = await client.PostAsync("/api/v1/telemetry/" + (batch ? "beacon" : "track") + "?k=alpha-key",
            new StringContent(batch ? "[" + payload + "]" : payload, Encoding.UTF8, "application/json"));
        Assert.Equal(batch ? HttpStatusCode.NoContent : HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(factory.Services.GetRequiredService<TelemetryChannelBroker>().Reader.TryRead(out var actual));
        Assert.Equal("", actual!.Measures!["amount"]);
        Assert.Equal(1, factory.Services.GetRequiredService<WriteAheadLogger>().FlushedRecordCount);
        string directory = factory.Services.GetRequiredService<IOptions<TelemetryEngineOptions>>().Value.WriteAheadLogDirectory;
        string line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(directory, "telemetry_wal.log")));
        Assert.Equal("", JsonSerializer.Deserialize(line, TelemetryAotContext.Default.GlobalTelemetryPayload)!.Measures!["amount"]);
    }

    [Theory]
    [InlineData("/health", true)]
    [InlineData("/api/v1/schema", false)]
    public async Task MiddlewareLeavesPublicPathsAndDisabledCatalogsAlone(string path, bool enabled)
    {
        var options = ProjectContextTests.Options();
        if (!enabled)
        {
            options.ProjectSchemas.Clear();
        }
        using var services = new ServiceCollection().AddSingleton(ProjectContextTests.Build(options)).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseProjectSchemas();
        int visited = 0;
        app.Run(context =>
        {
            visited++;
            context.Response.StatusCode = 209;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("project_id", "unknown")], "signed")) };
        context.Request.Path = path;
        await app.Build()(context);
        Assert.Equal(1, visited);
        Assert.Equal(209, context.Response.StatusCode);
        Assert.Empty(context.Items);
    }

    [Fact]
    public async Task TheLongestConfiguredProjectIdentifierKeepsItsOwnCatalog()
    {
        string id = new('x', 256);
        await using var factory = new ProjectContextHttpTests.Factory(customize: options =>
        {
            options.ProjectSchemas[0].ProjectId = id;
            options.IngestionKeys[0].ProjectId = id;
        });
        using var client = factory.CreateClient();
        using var request = ProjectContextHttpTests.Read("/api/v1/schema", id);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(id, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/v1/analytics/metrics/activity?filter=region:east")]
    [InlineData("/api/v1/analytics/metrics/activity?segment=local")]
    [InlineData("/api/v1/analytics/reports/overview")]
    public async Task LocalFiltersAndSegmentsReachStorageOnlyAfterContextualValidation(string path)
    {
        await using var factory = new ProjectContextHttpTests.Factory();
        using var client = factory.CreateClient();
        using var request = ProjectContextHttpTests.Read(path, "alpha");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public void KeyDiagnosticsCountPinnedCredentialsWithoutChangingTheirPolicy()
    {
        var options = ProjectContextTests.Options();
        options.AllowedProjectKeys = "free";
        options.IngestionKeys = [new() { Key = "a", ProjectId = "alpha", Scope = "server" }, new() { Key = "b", ProjectId = "beta", Scope = "public" }];
        var logger = new KeyLog();
        _ = new ApiKeyValidator(Microsoft.Extensions.Options.Options.Create(options), logger);
        var diagnostic = Assert.Single(logger.Messages);
        Assert.Equal(2, diagnostic["PinnedCount"]);
        Assert.Equal(3, diagnostic["KeyCount"]);
    }

    [Fact]
    public async Task AmbiguousDeliveryFencesEveryPhysicalAndLogicalProjectRollup()
    {
        var target = new AmbiguousWriter();
        await using var factory = new ProjectContextHttpTests.Factory(configure: services => services.AddSingleton<IDeliveryBatchWriter>(target));
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<ProjectCatalog>();
        target.SchemaFingerprint = TelemetryDeliverySchema.Fingerprint(new TelemetryColumnLayout(catalog.Storage.Dimensions, catalog.Storage.Measures));
        foreach (var rollup in catalog.Storage.Rollups.Declared)
        {
            catalog.Storage.Rollups.MarkRoutable(rollup);
        }
        var payload = new GlobalTelemetryPayload { ProjectId = "alpha", TenantId = "t", SessionId = "s", EventType = "e" };
        await factory.Services.GetRequiredService<WriteAheadLogger>().AppendAsync(payload, CancellationToken.None);
        var writer = factory.Services.GetRequiredService<ITelemetryBatchWriter>();
        Assert.Equal("ambiguous insert", (await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync([payload], 1, CancellationToken.None))).Message);
        Assert.All(catalog.Storage.Rollups.Declared, rollup => Assert.False(catalog.Storage.Rollups.IsRoutable(rollup)));
        foreach (var project in catalog.Declared)
        {
            Assert.Equal("a delivery has an uncertain materialized-view outcome", project.Rollups.UnroutableReason(Assert.Single(project.Rollups.Declared)));
        }
    }

    private sealed class AmbiguousWriter : IDeliveryBatchWriter
    {
        private readonly Guid _source = Guid.NewGuid();
        public string SchemaFingerprint { get; set; } = "";
        public Task<Guid> GetSourceIdAsync(CancellationToken cancellationToken) => Task.FromResult(_source);
        public Task WriteDeliveryAsync(TelemetryDeliveryUnit unit, CancellationToken cancellationToken) => throw new IOException("ambiguous insert");
    }

    [Theory]
    [InlineData("alpha-key", "alpha", 200)]
    [InlineData("beta-key", "beta", 400)]
    [InlineData("alpha-key", "beta", 403)]
    [InlineData("alpha-public", "alpha", 403)]
    [InlineData("unknown-key", "unknown", 403)]
    public async Task TenantAttributeWritesKeepThePinnedProjectsLogicalAllowlist(string key, string project, int status)
    {
        var target = new AttributeCapture();
        await using var factory = new ProjectContextHttpTests.Factory(configure: services => services.AddSingleton<ITenantAttributeWriter>(target));
        using var client = factory.CreateClient();
        string body = JsonSerializer.Serialize(new
        {
            projectId = project,
            tenants = new[] { new { tenantId = "t", attributes = new Dictionary<string, string> { ["cohort"] = "group" } } }
        });
        using var response = await client.PostAsync("/api/v1/tenants/attributes?k=" + key, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 200)
        {
            Assert.Equal("alpha", Assert.Single(target.Projects));
            Assert.Equal("cohort", Assert.Single(target.Rows).Attribute);
            Assert.Equal("group", target.Rows[0].Value);
            Assert.Equal("t", target.Rows[0].TenantId);
        }
        else
        {
            Assert.Empty(target.Projects);
            Assert.Empty(target.Rows);
        }
    }

    private sealed class AttributeCapture : ITenantAttributeWriter
    {
        internal List<string> Projects { get; } = [];
        internal List<TenantAttributeRow> Rows { get; } = [];
        public Task WriteAsync(string project, IReadOnlyList<TenantAttributeRow> rows, DateTime updatedAt, CancellationToken cancellationToken)
        {
            Projects.Add(project);
            Rows.AddRange(rows);
            return Task.CompletedTask;
        }
    }

    private sealed class KeyLog : ILogger<ApiKeyValidator>
    {
        internal List<Dictionary<string, object?>> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (id.Id == 1002)
            {
                Messages.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value));
            }
        }
    }
}

[CollectionDefinition("Project context hosts", DisableParallelization = true)]
public sealed class ProjectContextHostCollection;

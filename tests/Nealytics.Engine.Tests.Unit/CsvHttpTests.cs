using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Nealytics.Engine.Features.ImportCsv;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Project context hosts")]
public sealed class CsvHttpTests
{
    internal const string Route = "/api/v1/telemetry/import/csv";
    internal const string Header = "id,time,tenant,tag\r\n";
    internal static string Row(string tag = "\"日本語, \"\"quoted\"\"\r\nnext\"", string tenant = "t") =>
        KafkaTestSupport.Identity + ",2026-01-01T00:00:00Z," + tenant + "," + tag;

    internal static NdjsonImportHttpTests.Factory Host(string path = "managed", bool kestrel = false,
        Action<TelemetryEngineOptions>? configure = null) => new(path: path, enabled: false, kestrel: kestrel, configure: options =>
        {
            options.EnableCsvImport = true;
            var mapping = CsvMappingTests.Options();
            mapping.Fields["dimensions.tag"] = new() { Path = ["tag"] };
            options.CsvSourceMappings["p"] = mapping;
            options.Dimensions = [new() { Name = "tag" }];
            configure?.Invoke(options);
        });

    internal static HttpRequestMessage Request(string body, string? query = "server-key", string? key = null) =>
        CreateRequest(new StringContent(body, Encoding.UTF8, "text/csv"), query, key);

    private static HttpRequestMessage CreateRequest(HttpContent content, string? query, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route + (query is null ? "" : "?k=" + query)) { Content = content };
        if (key is not null) request.Headers.Add("X-Project-Key", key);
        return request;
    }

    [Theory]
    [InlineData("managed", false)]
    [InlineData("custom", false)]
    [InlineData("managed", true)]
    [InlineData("custom", true)]
    public async Task CsvOnlyHostsPersistMultilineMappedRecordsAndReturnDecodedByteOffsets(string path, bool kestrel)
    {
        await using var host = Host(path, kestrel);
        string body = "\uFEFF" + Header + Row() + "\n" + Row("last");
        using var response = await host.Client.SendAsync(Request(body));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = JsonSerializer.Deserialize<CsvImportResponse>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(new CsvImportResponse(2, Encoding.UTF8.GetByteCount(body), null), receipt);
        Assert.Equal(3, receipt.NextRecord);
        Assert.True(receipt.Complete);
        Assert.Equal(2, host.Wal.UncommittedRecordCount);
        var broker = host.Services.GetRequiredService<TelemetryChannelBroker>();
        Assert.True(broker.Reader.TryRead(out var first));
        Assert.Equal("日本語, \"quoted\"\r\nnext", first!.Dimensions!["tag"]);
        Assert.Equal("p", first.ProjectId);
        Assert.True(broker.Reader.TryRead(out var last));
        Assert.Equal("last", last!.Dimensions!["tag"]);
        Assert.False(broker.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("", "header_required", 0)]
    [InlineData("id,time\n", "invalid_header", 0)]
    [InlineData(Header, null, 0)]
    [InlineData(Header + "bad,row\n", "column_count", 0)]
    [InlineData(Header + "bad,2026-01-01T00:00:00Z,t,x\n", "invalid_mapping", 0)]
    [InlineData(Header + "\"unterminated", "invalid_csv", 0)]
    public async Task InvalidHeadersOrRowsProduceBoundedReceiptsWithoutInventedAdmissions(string body, string? error, int count)
    {
        await using var host = Host();
        using var response = await host.Client.SendAsync(Request(body));
        Assert.Equal(error is null ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(count, document.RootElement.GetProperty("acceptedRecords").GetInt32());
        Assert.Equal(error is null, document.RootElement.GetProperty("complete").GetBoolean());
        if (error is not null) Assert.Equal(error, document.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, host.Wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("custom")]
    public async Task InvalidRowKeepsOnlyTheDurablyAcceptedPrefix(string path)
    {
        await using var host = Host(path);
        string prefix = Header + Row() + "\r\n";
        using var response = await host.Client.SendAsync(Request(prefix + "bad,row\n" + Row("unreachable")));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, receipt.RootElement.GetProperty("acceptedRecords").GetInt32());
        Assert.Equal(Encoding.UTF8.GetByteCount(prefix), receipt.RootElement.GetProperty("nextByteOffset").GetInt64());
        Assert.Equal("column_count", receipt.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, host.Wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData(null, null, 401)]
    [InlineData("unknown", null, 401)]
    [InlineData("public-key", null, 403)]
    [InlineData("unpinned-key", null, 403)]
    [InlineData("server-key", "unknown", 401)]
    [InlineData("unknown", "server-key", 202)]
    public async Task AuthenticationAndHeaderPrecedenceMatchExistingImports(string? query, string? key, int status)
    {
        await using var host = Host();
        using var response = await host.Client.SendAsync(Request(Header, query, key));
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    public async Task DisabledRoutesAndUnmappedProjectsRemainInaccessible()
    {
        await using (var disabled = new NdjsonImportHttpTests.Factory(enabled: false))
        {
            using var missing = await disabled.Client.SendAsync(Request(Header));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        await using var host = Host(configure: options => options.CsvSourceMappings.Clear());
        using var unmapped = await host.Client.SendAsync(Request(Header));
        Assert.Equal(HttpStatusCode.NotFound, unmapped.StatusCode);
    }

    [Theory]
    [InlineData("text/csv", true)]
    [InlineData("TEXT/CSV; charset=\"UTF-8\"; header=\"present\"", true)]
    [InlineData("text/csv; header=PRESENT", true)]
    [InlineData("text/csv; unknown=value", true)]
    [InlineData("text/csv; header=absent", false)]
    [InlineData("text/csv; header=present; header=absent", false)]
    [InlineData("text/csv; charset=utf-16", false)]
    [InlineData("application/x-ndjson", false)]
    [InlineData("invalid", false)]
    [InlineData(null, false)]
    public void ContentTypeRequiresExplicitCsvUtf8AndAnAvailableHeader(string? value, bool accepted) =>
        Assert.Equal(accepted, ImportCsvEndpoint.AcceptsContentType(value));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactRecordLimitCompletesButAnAdditionalRowStopsAtTheConfirmedBoundary(bool extra)
    {
        await using var host = Host(configure: options => options.ImportMaxRecords = 1);
        string prefix = Header + Row("one") + "\n";
        using var response = await host.Client.SendAsync(Request(prefix + (extra ? Row("two") : "")));
        Assert.Equal(extra ? HttpStatusCode.BadRequest : HttpStatusCode.Accepted, response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, receipt.RootElement.GetProperty("acceptedRecords").GetInt32());
        Assert.Equal(Encoding.UTF8.GetByteCount(prefix), receipt.RootElement.GetProperty("nextByteOffset").GetInt64());
        Assert.Equal(!extra, receipt.RootElement.GetProperty("complete").GetBoolean());
        if (extra) Assert.Equal("record_limit", receipt.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, host.Wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task ExistingEventPolicyRejectsMappedRowsBeforePersistence()
    {
        await using var host = Host(configure: options => options.CsvSourceMappings["p"].Fields["eventType"].Default = "forbidden");
        using var response = await host.Client.SendAsync(Request(Header + Row()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, receipt.RootElement.GetProperty("acceptedRecords").GetInt32());
        Assert.False(receipt.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(0, host.Wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownBodyAndLogicalRecordBudgetsHaveDistinctOversizeReceipts(bool recordBudget)
    {
        await using var host = Host();
        string body = recordBudget ? Header + Row(new string('x', 1024)) : new string('x', 4097);
        using var response = await host.Client.SendAsync(Request(body));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(recordBudget ? "record_too_large" : "body_too_large", receipt.RootElement.GetProperty("error").GetString());
        Assert.Equal(recordBudget ? Encoding.UTF8.GetByteCount(Header) : 0, receipt.RootElement.GetProperty("nextByteOffset").GetInt64());
        Assert.Equal(0, host.Wal.UncommittedRecordCount);
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("custom")]
    public async Task CompressedInputUsesTheDecodedBodyBudgetAndRetainsItsActualPrefix(string path)
    {
        await using var host = Host(path, configure: options => options.ImportMaxRecords = 1000);
        string body = Header + string.Concat(Enumerable.Repeat(Row("data") + "\n", 100));
        using var memory = new MemoryStream();
        using (var compressor = new System.IO.Compression.GZipStream(memory, System.IO.Compression.CompressionLevel.Fastest, true))
            await compressor.WriteAsync(Encoding.UTF8.GetBytes(body));
        using var request = Request("");
        request.Content = new ByteArrayContent(memory.ToArray());
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/csv");
        request.Content.Headers.ContentEncoding.Add("gzip");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("body_too_large", receipt.RootElement.GetProperty("error").GetString());
        Assert.Equal(host.Wal.UncommittedRecordCount, receipt.RootElement.GetProperty("acceptedRecords").GetInt64());
    }

    [Fact]
    public async Task UnsupportedContentTypesAreRejectedBeforeReadingCsv()
    {
        await using var host = Host();
        using var request = Request(Header + Row());
        request.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, host.Wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task CsvAndNdjsonShareOneQueueFreePermitWhileOrdinaryTrackRemainsAvailable()
    {
        await using var host = Host(kestrel: true, configure: options => options.EnableNdjsonImport = true);
        using var held = new HeldContent(Header + Row("first") + "\n");
        held.Headers.ContentType = MediaTypeHeaderValue.Parse("text/csv");
        using var first = new HttpRequestMessage(HttpMethod.Post, Route + "?k=server-key") { Content = held };
        var pending = host.Client.SendAsync(first);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (host.Wal.UncommittedRecordCount != 1) await Task.Delay(1, deadline.Token);
            using var second = await host.Client.SendAsync(Request(Header), deadline.Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
            using var ndjson = await host.Client.PostAsync("/api/v1/telemetry/import/ndjson?k=server-key",
                new StringContent(NdjsonRecordReaderTests.Event(), Encoding.UTF8, "application/x-ndjson"), deadline.Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, ndjson.StatusCode);
            using var track = await host.Client.PostAsync("/api/v1/telemetry/track?k=server-key",
                new StringContent(NdjsonRecordReaderTests.Event(), Encoding.UTF8, "application/json"), deadline.Token);
            Assert.Equal(HttpStatusCode.Accepted, track.StatusCode);
        }
        finally
        {
            held.Release.TrySetResult();
            using var completed = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.Accepted, completed.StatusCode);
        }
        using var following = await host.Client.SendAsync(Request(Header));
        Assert.Equal(HttpStatusCode.Accepted, following.StatusCode);
        Assert.Equal(2, host.Wal.UncommittedRecordCount);
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

    [Fact]
    public async Task CsvAdmissionAppliesTheBudgetToTheHostBodyLimitFeature()
    {
        await using var host = Host();
        _ = host.Client;
        using var scope = host.Services.CreateScope();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?k=server-key");
        context.Request.ContentType = "text/csv";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Header));
        context.Response.Body = new MemoryStream();
        var feature = new MutableBodyLimit();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>(feature);
        var endpoint = Assert.Single(host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == Route);
        await endpoint.RequestDelegate!(context);
        Assert.Equal(202, context.Response.StatusCode);
        Assert.Equal(4096, feature.MaxRequestBodySize);
    }

    private sealed class MutableBodyLimit : Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    [Fact]
    public async Task RouteMetadataUsesTheSharedQueueFreeImportAdmissionPolicy()
    {
        await using var host = Host();
        _ = host.Client;
        var endpoint = Assert.Single(host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == Route);
        Assert.Equal("ndjson-import", endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()!.PolicyName);
        Assert.Equal("ImportCsv", endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName);
    }
}

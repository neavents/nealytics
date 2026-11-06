using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Nealytics.Engine.Features.GcsImport;
using static Nealytics.Engine.Tests.Unit.GcsTestSupport;
using Handler = Nealytics.Engine.Tests.Unit.S3TransportTestSupport.Handler;
using TrackingStream = Nealytics.Engine.Tests.Unit.S3TransportTestSupport.TrackingStream;
using UnknownLengthContent = Nealytics.Engine.Tests.Unit.S3TransportTestSupport.UnknownLengthContent;

namespace Nealytics.Engine.Tests.Unit;

public sealed class GcsObjectClientTests
{
    [Fact]
    public async Task InspectionUsesOnlyConfiguredSelectionHeadersAndDisposesMetadataWithoutReadingItsBody()
    {
        using var catalog = Catalog(); var source = catalog.Find("source", "p")!;
        var stream = new TrackingStream([]);
        var handler = new Handler(_ => { var response = Metadata(17); response.Content = new StreamContent(stream); response.Content.Headers.ContentLength = 17; return response; });
        using var client = GcsTestSupport.Client(handler);
        var snapshot = await client.InspectAsync(source, new("prefix/file.ndjson", null), default);
        Assert.Equal(GcsTestSupport.Snapshot(source, 17), snapshot);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Head, request.Method); Assert.Equal(GcsObjectAddress.Create(source, "prefix/file.ndjson", null), request.RequestUri);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=GOOGTESTACCESS/20130524/auto/s3/aws4_request,",
            Assert.Single(request.Headers.GetValues("Authorization")));
        Assert.False(request.Headers.Contains("x-amz-security-token"));
        Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).Value);
        Assert.Null(request.Headers.Range); Assert.Empty(request.Headers.IfMatch);
        Assert.True(stream.Disposed); Assert.Equal(0, stream.Reads);
        client.Dispose(); Assert.True(handler.Disposed);
    }

    [Theory]
    [InlineData("x-goog-generation", null)]
    [InlineData("x-goog-generation", "invalid")]
    [InlineData("x-goog-generation", "0")]
    [InlineData("x-goog-generation", "-1")]
    [InlineData("x-goog-generation", "9223372036854775808")]
    [InlineData("x-goog-generation", " 17")]
    [InlineData("x-goog-generation", "+17")]
    [InlineData("x-goog-metageneration", null)]
    [InlineData("x-goog-metageneration", "0")]
    [InlineData("x-goog-stored-content-length", null)]
    [InlineData("x-goog-stored-content-length", "-1")]
    [InlineData("x-goog-stored-content-length", "unbounded")]
    public async Task ProviderNumericHeadersMustBeSingleBoundedNonnegativeValues(string name, string? value)
    {
        using var source = Source(); var response = Metadata(5); response.Headers.Remove(name);
        if (value is not null) response.Headers.Add(name, value);
        using var client = GcsTestSupport.Client(new Handler(_ => response));
        var error = await Assert.ThrowsAsync<GcsImportException>(() => client.InspectAsync(source, new("key", null), default));
        Assert.Equal(502, error.StatusCode); Assert.Equal("source_protocol_error", error.Error);
    }

    [Theory]
    [InlineData("x-goog-generation")]
    [InlineData("x-goog-metageneration")]
    [InlineData("x-goog-stored-content-length")]
    [InlineData("x-goog-stored-content-encoding")]
    public async Task DuplicateProviderMetadataNeverSuppliesAnAmbiguousRepresentation(string name)
    {
        using var source = Source(); var response = Metadata(5);
        response.Headers.TryAddWithoutValidation(name, "17");
        using var client = GcsTestSupport.Client(new Handler(_ => response));
        Assert.Equal("source_protocol_error", (await Assert.ThrowsAsync<GcsImportException>(() =>
            client.InspectAsync(source, new("key", null), default))).Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("gzip")]
    public async Task MissingOrCompressedStoredRepresentationsCannotBeTranscodedIntoAResumableSource(string? stored)
    {
        using var source = Source(); var response = Metadata(5);
        response.Headers.Remove("x-goog-stored-content-encoding");
        if (stored is not null) response.Headers.Add("x-goog-stored-content-encoding", stored);
        using var client = GcsTestSupport.Client(new Handler(_ => response));
        var error = await Assert.ThrowsAsync<GcsImportException>(() => client.InspectAsync(source, new("key", null), default));
        Assert.Equal(415, error.StatusCode); Assert.Equal("source_encoding_unsupported", error.Error);
    }

    [Fact]
    public async Task InspectionPinsRequestedGenerationsAndNeverReinterpretsAProviderMismatch()
    {
        using var source = Source(); var handler = new Handler(_ => Metadata(5));
        using var client = GcsTestSupport.Client(handler);
        var error = await Assert.ThrowsAsync<GcsImportException>(() => client.InspectAsync(source, new("key", 18), default));
        Assert.Equal(409, error.StatusCode); Assert.Equal("source_changed", error.Error);
        Assert.Equal("?generation=18", Assert.Single(handler.Requests).RequestUri!.Query);
    }

    [Theory]
    [InlineData("x-goog-generation", "18")]
    [InlineData("x-goog-metageneration", "4")]
    [InlineData("x-goog-stored-content-length", "6")]
    public async Task RangeResponsesMustRetainEverySelectedProviderIdentityBeforeBodyRead(string name, string value)
    {
        using var source = Source(); var stream = new TrackingStream("a\nb"u8.ToArray());
        var response = Response("a\nb", 5, stream: stream); response.Headers.Remove(name); response.Headers.Add(name, value);
        var handler = new Handler(_ => response); using var client = GcsTestSupport.Client(handler, bytes: 3);
        var error = await Assert.ThrowsAsync<GcsImportException>(() => client.ReadChunkAsync(source, Snapshot(source, 5), 0, null, default));
        Assert.Equal(409, error.StatusCode); Assert.Equal("source_changed", error.Error);
        Assert.Equal(0, stream.Reads); Assert.True(stream.Disposed);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("?generation=17", request.RequestUri!.Query);
        Assert.Equal("17", Assert.Single(request.Headers.GetValues("x-goog-if-generation-match")));
        Assert.Equal("3", Assert.Single(request.Headers.GetValues("x-goog-if-metageneration-match")));
        string signed = Assert.Single(request.Headers.GetValues("Authorization"));
        Assert.Contains("x-goog-if-generation-match;x-goog-if-metageneration-match", signed);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("key", 0L)]
    [InlineData("key", -1L)]
    public async Task InvalidSelectorsCannotMakeAnInspectionRequest(string? key, long? generation)
    {
        using var source = Source(); var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var client = GcsTestSupport.Client(handler);
        var error = await Assert.ThrowsAsync<GcsImportException>(() => client.InspectAsync(source, new(key!, generation), default));
        Assert.Equal(400, error.StatusCode); Assert.Equal("invalid_object_request", error.Error); Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MinimumPositiveGenerationsAndMaximumInt64MetadataRemainRepresentable()
    {
        using var source = Source();
        foreach (long number in new[] { 1L, long.MaxValue })
        {
            var response = Metadata(0); response.Headers.Remove("x-goog-generation"); response.Headers.Remove("x-goog-metageneration");
            response.Headers.Add("x-goog-generation", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            response.Headers.Add("x-goog-metageneration", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var client = GcsTestSupport.Client(new Handler(_ => response));
            var snapshot = await client.InspectAsync(source, new("key", number), default);
            Assert.True(snapshot.IsValid(source)); Assert.Equal(number, snapshot.Generation); Assert.Equal(number, snapshot.Metageneration);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("weak")]
    [InlineData("wildcard")]
    [InlineData("duplicate")]
    [InlineData("length")]
    [InlineData("encoding")]
    [InlineData("large")]
    public async Task InvalidOrUnboundedInspectionMetadataCannotCreateASnapshot(string invalid)
    {
        var declaration = Options(); declaration.MaxObjectBytes = 10;
        using var catalog = Catalog(declaration); var source = catalog.Find("source", "p")!;
        var response = Metadata(5);
        switch (invalid)
        {
            case "missing": response.Headers.Remove("ETag"); break;
            case "weak": response.Headers.ETag = new EntityTagHeaderValue("\"tag\"", true); break;
            case "wildcard": response.Headers.ETag = EntityTagHeaderValue.Any; break;
            case "duplicate": response.Headers.TryAddWithoutValidation("ETag", "\"other\""); break;
            case "length": response.Content = new UnknownLengthContent(); break;
            case "encoding": response.Content.Headers.ContentEncoding.Add("gzip"); break;
            case "large": response.Content.Headers.ContentLength = 11; response.Headers.Remove("x-goog-stored-content-length"); response.Headers.Add("x-goog-stored-content-length", "11"); break;
        }
        using var client = GcsTestSupport.Client(new Handler(_ => response));
        var error = await Assert.ThrowsAsync<GcsImportException>(() => client.InspectAsync(source, new("prefix/file.ndjson", null), default));
        Assert.Equal(invalid == "encoding" ? 415 : invalid == "large" ? 413 : 502, error.StatusCode);
        Assert.Equal(invalid == "encoding" ? "source_encoding_unsupported" : invalid == "large" ? "source_too_large" : "source_protocol_error", error.Error);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(301, 502, "source_redirect_refused")]
    [InlineData(307, 502, "source_redirect_refused")]
    [InlineData(401, 502, "source_access_denied")]
    [InlineData(403, 502, "source_access_denied")]
    [InlineData(404, 404, "source_object_not_found")]
    [InlineData(412, 409, "source_changed")]
    [InlineData(416, 409, "source_changed")]
    [InlineData(500, 502, "source_unavailable")]
    [InlineData(201, 502, "source_protocol_error")]
    public async Task BothOperationsUseFixedSafeStatusDiagnostics(int remote, int expected, string code)
    {
        using var catalog = Catalog(); var source = catalog.Find("source", "p")!;
        using var client = GcsTestSupport.Client(new Handler(_ => new HttpResponseMessage((HttpStatusCode)remote)
        { Content = new StringContent("secret remote body") }));
        var inspect = await Assert.ThrowsAsync<GcsImportException>(() => client.InspectAsync(source, new("prefix/file.ndjson", null), default));
        var chunk = await Assert.ThrowsAsync<GcsImportException>(() => client.ReadChunkAsync(source, GcsTestSupport.Snapshot(source, 3), 0, null, default));
        Assert.Equal(expected, inspect.StatusCode); Assert.Equal(code, inspect.Error);
        Assert.Equal(expected, chunk.StatusCode); Assert.Equal(code, chunk.Error);
        Assert.DoesNotContain("secret", inspect.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalReadsKeepExactRangesAndFrameOnlyCompleteRecords(bool exact)
    {
        using var catalog = Catalog(); var source = catalog.Find("source", "p")!;
        var stream = new TrackingStream("a\r\nb\nc"u8.ToArray(), 1);
        var handler = new Handler(_ => Response("a\r\nb\nc", 6, stream: stream));
        using var client = GcsTestSupport.Client(handler, bytes: 6, records: 1);
        using var chunk = await client.ReadChunkAsync(source, GcsTestSupport.Snapshot(source, 6), 0, exact ? 6 : null, default);
        Assert.Equal(exact ? "a\r\nb\nc" : "a\r\n", Encoding.UTF8.GetString(chunk.Bytes.Span));
        Assert.Equal("\"tag\"", Assert.Single(handler.Requests[0].Headers.IfMatch).Tag);
        var range = Assert.Single(handler.Requests[0].Headers.Range!.Ranges);
        Assert.Equal(0, range.From); Assert.Equal(5, range.To);
        Assert.True(stream.Disposed); Assert.Equal(7, stream.Reads);
    }

    [Theory]
    [InlineData("tag", 409, "source_changed")]
    [InlineData("range", 502, "source_protocol_error")]
    [InlineData("unit", 502, "source_protocol_error")]
    [InlineData("length", 502, "source_protocol_error")]
    [InlineData("short", 502, "source_protocol_error")]
    [InlineData("long", 502, "source_protocol_error")]
    [InlineData("encoding", 415, "source_encoding_unsupported")]
    [InlineData("no-newline", 413, "source_record_too_large")]
    public async Task InvalidRangesOrBodiesAreRejectedBeforeAnyRecordIsReturned(string invalid, int status, string code)
    {
        using var catalog = Catalog(); var source = catalog.Find("source", "p")!;
        var response = Response(invalid == "no-newline" ? "abc" : "a\nb", 5);
        switch (invalid)
        {
            case "tag": response.Headers.ETag = new("\"other\""); break;
            case "range": response.Content.Headers.ContentRange = null; break;
            case "unit": response.Content.Headers.ContentRange!.Unit = "items"; break;
            case "length": response.Content.Headers.ContentLength = 2; break;
            case "short": response.Content = new StreamContent(new TrackingStream("a\n"u8.ToArray())); response.Content.Headers.ContentLength = 3; response.Content.Headers.ContentRange = new(0, 2, 5); break;
            case "long": response.Content = new StreamContent(new TrackingStream("a\nbc"u8.ToArray())); response.Content.Headers.ContentLength = 3; response.Content.Headers.ContentRange = new(0, 2, 5); break;
            case "encoding": response.Content.Headers.ContentEncoding.Add("br"); break;
        }
        using var client = GcsTestSupport.Client(new Handler(_ => response), bytes: 3);
        var error = await Assert.ThrowsAsync<GcsImportException>(() => client.ReadChunkAsync(source, GcsTestSupport.Snapshot(source, 5), 0, null, default));
        Assert.Equal(status, error.StatusCode); Assert.Equal(code, error.Error);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("caller")]
    public async Task TransportFailuresAndCallerCancellationHaveDistinctOwnership(string fault)
    {
        using var catalog = Catalog(); var source = catalog.Find("source", "p")!;
        using var caller = new CancellationTokenSource();
        var handler = new Handler((_, _) =>
        {
            if (fault == "caller") caller.Cancel();
            return Task.FromException<HttpResponseMessage>(fault is "caller" or "timeout" ? new OperationCanceledException()
                : fault == "http" ? new HttpRequestException("secret address") : new IOException("secret body"));
        });
        using var client = GcsTestSupport.Client(handler);
        if (fault == "caller") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.InspectAsync(source, new("prefix/file.ndjson", null), caller.Token));
        else
        {
            var error = await Assert.ThrowsAsync<GcsImportException>(() => client.InspectAsync(source, new("prefix/file.ndjson", null), caller.Token));
            Assert.Equal(fault == "timeout" ? 504 : 502, error.StatusCode);
            Assert.Equal(fault == "timeout" ? "source_timeout" : "source_unavailable", error.Error);
            Assert.DoesNotContain("secret", error.ToString()); Assert.Null(error.InnerException);
        }
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(4, null)]
    [InlineData(0, -1)]
    [InlineData(0, 17)]
    [InlineData(0, 4)]
    [InlineData(1, 3)]
    [InlineData(0, 0)]
    public async Task InvalidCallerReadBoundsNeverSendARequest(long offset, int? exact)
    {
        using var sources = Catalog(); var source = sources.Find("source", "p")!;
        var handler = new Handler(_ => throw new InvalidOperationException("must not fetch"));
        using var client = GcsTestSupport.Client(handler);
        var error = await Assert.ThrowsAsync<GcsImportException>(() => client.ReadChunkAsync(source,
            GcsTestSupport.Snapshot(source, 3), offset, exact, default));
        Assert.Equal(400, error.StatusCode); Assert.Equal("invalid_object_request", error.Error); Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("total")]
    public async Task EveryRangeCoordinateMustMatchTheRequestedRepresentation(string part)
    {
        using var sources = Catalog(); var source = sources.Find("source", "p")!;
        var response = Response("a\nb", 5, 1);
        response.Content.Headers.ContentRange = part switch
        {
            "start" => new ContentRangeHeaderValue(0, 2, 5),
            "end" => new ContentRangeHeaderValue(1, 2, 5),
            _ => new ContentRangeHeaderValue(1, 3, 6)
        };
        using var client = GcsTestSupport.Client(new Handler(_ => response), bytes: 3);
        Assert.Equal("source_protocol_error", (await Assert.ThrowsAsync<GcsImportException>(() => client.ReadChunkAsync(
            source, GcsTestSupport.Snapshot(source, 5), 1, null, default))).Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task MetadataAcceptsEmptyObjectsAndTheInclusiveSizeLimitWithIdentityEncoding(long bytes)
    {
        var options = Options(); options.MaxObjectBytes = 10;
        using var catalog = Catalog(options); var source = catalog.Find("source", "p")!;
        using var client = GcsTestSupport.Client(new Handler(_ =>
        {
            var response = Metadata(bytes);
            response.Content.Headers.ContentEncoding.Add("IDENTITY"); response.Headers.Remove("x-goog-stored-content-encoding");
            response.Headers.Add("x-goog-stored-content-encoding", "IDENTITY"); return response;
        }));
        Assert.Equal(GcsTestSupport.Snapshot(source, bytes), await client.InspectAsync(source, new("prefix/file.ndjson", null), default));
    }

    [Theory]
    [InlineData(200, "source_protocol_error")]
    [InlineData(300, "source_redirect_refused")]
    [InlineData(400, "source_unavailable")]
    public async Task RangeReadsNeverAcceptIgnoredRangesOrFollowStatusClassBoundaries(int status, string code)
    {
        using var catalog = Catalog(); var source = catalog.Find("source", "p")!;
        using var client = GcsTestSupport.Client(new Handler(_ => new HttpResponseMessage((HttpStatusCode)status)));
        var error = await Assert.ThrowsAsync<GcsImportException>(() => client.ReadChunkAsync(source,
            GcsTestSupport.Snapshot(source, 3), 0, null, default));
        Assert.Equal(502, error.StatusCode); Assert.Equal(code, error.Error);
    }

    [Fact]
    public async Task ATruncatedBodyReturnsEveryRentedBufferWithClearingRequested()
    {
        using var catalog = Catalog(); var source = catalog.Find("source", "p")!;
        var pool = new ImportJobCoordinatorTests.TrackingPool();
        var response = Response("a\nb", 3, stream: new TrackingStream("a"u8.ToArray()));
        using var client = GcsTestSupport.Client(new Handler(_ => response), pool: pool);
        var error = await Assert.ThrowsAsync<GcsImportException>(() => client.ReadChunkAsync(source,
            GcsTestSupport.Snapshot(source, 3), 0, null, default));
        Assert.Equal("source_protocol_error", error.Error); Assert.NotEmpty(pool.Rented);
        Assert.Equal(pool.Rented.Count, pool.Returned.Count); Assert.All(pool.Returned, clear => Assert.True(clear));
    }

    [Fact]
    public async Task EmptyObjectsUseConditionalHeadAndNonzeroFinalOffsetsAreNotNewChunks()
    {
        using var catalog = Catalog(); var source = catalog.Find("source", "p")!;
        var handler = new Handler(_ => Response());
        using var client = GcsTestSupport.Client(handler);
        using var empty = await client.ReadChunkAsync(source, GcsTestSupport.Snapshot(source, 0), 0, null, default);
        Assert.Empty(empty.Bytes.ToArray()); Assert.Equal(HttpMethod.Head, handler.Requests[0].Method);
        await Assert.ThrowsAsync<GcsImportException>(() => client.ReadChunkAsync(source, GcsTestSupport.Snapshot(source, 3), 3, null, default));
        Assert.Single(handler.Requests);
    }
}

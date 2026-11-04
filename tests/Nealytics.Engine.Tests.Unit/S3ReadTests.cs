using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Nealytics.Engine.Features.S3Import;
using static Nealytics.Engine.Tests.Unit.S3TransportTestSupport;

namespace Nealytics.Engine.Tests.Unit;

public sealed class S3ReadTests
{
    [Fact]
    public async Task ConditionalRangesAreBoundedFramedAndOwnedIndependentlyOfTheTransport()
    {
        const string body = "a\r\nb\nXYZ";
        var source = S3TestSupport.Source();
        var stream = new TrackingStream(Encoding.UTF8.GetBytes(body), fragment: 2);
        var handler = new Handler(_ => Response(body, 20, 3, stream: stream));
        var pool = new ImportJobCoordinatorTests.TrackingPool();
        using var client = Client(handler, bytes: 8, pool: pool);
        var chunk = await client.ReadChunkAsync(source, Snapshot(source, 20), 3, null, CancellationToken.None);
        Assert.True(stream.Disposed);
        Assert.Equal("a\r\nb\n", Encoding.UTF8.GetString(chunk.Bytes.Span));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("bytes=3-10", request.Headers.Range!.ToString());
        Assert.Equal("\"tag\"", Assert.Single(request.Headers.IfMatch).Tag);
        Assert.Contains("SignedHeaders=host;if-match;range;x-amz-content-sha256;x-amz-date", Assert.Single(request.Headers.GetValues("Authorization")));
        client.Dispose();
        Assert.True(handler.Disposed);
        Assert.Equal(5, chunk.Bytes.Length);
        chunk.Dispose();
        Assert.NotEmpty(pool.Rented);
        Assert.Equal(pool.Rented.Count, pool.Returned.Count);
        Assert.All(pool.Returned, clear => Assert.True(clear));
    }

    [Theory]
    [InlineData("a\nb", 2, null, "a\nb")]
    [InlineData("a\nb\nc", 1, null, "a\n")]
    [InlineData("a\nb\nc", 1, 5, "a\nb\nc")]
    public async Task FinalLinesAndConfirmedRetryLengthsKeepTheirOriginalBytes(string body, int records, int? exact, string expected)
    {
        var source = S3TestSupport.Source();
        var handler = new Handler(_ => Response(body, Encoding.UTF8.GetByteCount(body)));
        using var client = Client(handler, records: records);
        using var chunk = await client.ReadChunkAsync(source, Snapshot(source, Encoding.UTF8.GetByteCount(body)), 0, exact, CancellationToken.None);
        Assert.Equal(expected, Encoding.UTF8.GetString(chunk.Bytes.Span));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task EmptySnapshotsStillRequireAConditionalHeadAndOwnNoRentedBody(int? exact)
    {
        var source = S3TestSupport.Source();
        var handler = new Handler(_ => Response());
        var pool = new ImportJobCoordinatorTests.TrackingPool();
        using var client = Client(handler, pool: pool);
        using var chunk = await client.ReadChunkAsync(source, Snapshot(source, 0), 0, exact, CancellationToken.None);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Head, request.Method);
        Assert.Null(request.Headers.Range);
        Assert.Single(request.Headers.IfMatch);
        Assert.Empty(chunk.Bytes.ToArray());
        Assert.Empty(pool.Rented);
        Assert.Empty(pool.Returned);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("offset-negative")]
    [InlineData("offset-past")]
    [InlineData("offset-end")]
    [InlineData("length-negative")]
    [InlineData("length-limit")]
    [InlineData("length-source")]
    [InlineData("length-remaining")]
    [InlineData("length-zero")]
    public async Task InvalidReadBoundariesNeverOpenANetworkRequest(string scenario)
    {
        var source = S3TestSupport.Source();
        var snapshot = Snapshot(source, 3);
        long offset = scenario switch { "offset-negative" => -1, "offset-past" => 4, "offset-end" => 3, "length-remaining" => 1, _ => 0 };
        int? exact = scenario switch { "length-negative" => -1, "length-limit" => 17, "length-source" => 4, "length-remaining" => 3, "length-zero" => 0, _ => null };
        if (scenario == "identity") snapshot = snapshot with { SourceId = "wrong" };
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<S3ImportException>(() => client.ReadChunkAsync(source, snapshot, offset, exact, CancellationToken.None));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid_object_request", error.Error);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("etag", 409, "source_changed")]
    [InlineData("version", 409, "source_changed")]
    [InlineData("length", 502, "source_protocol_error")]
    [InlineData("range-missing", 502, "source_protocol_error")]
    [InlineData("range-unit", 502, "source_protocol_error")]
    [InlineData("range-start", 502, "source_protocol_error")]
    [InlineData("range-end", 502, "source_protocol_error")]
    [InlineData("range-total", 502, "source_protocol_error")]
    [InlineData("range-ignored", 502, "source_protocol_error")]
    [InlineData("encoding", 415, "source_encoding_unsupported")]
    public async Task AllRangeMetadataIsValidatedBeforeReadingTheBody(string scenario, int status, string expected)
    {
        var source = S3TestSupport.Source();
        var stream = new TrackingStream("{}\n"u8.ToArray());
        var handler = new Handler(_ =>
        {
            var response = Response("{}\n", 3, stream: stream);
            switch (scenario)
            {
                case "etag": response.Headers.Remove("ETag"); break;
                case "version": response.Headers.Remove("x-amz-version-id"); break;
                case "length": response.Content.Headers.ContentLength = 4; break;
                case "range-missing": response.Content.Headers.ContentRange = null; break;
                case "range-unit": response.Content.Headers.ContentRange!.Unit = "other"; break;
                case "range-start": response.Content.Headers.ContentRange = new(1, 2, 3); break;
                case "range-end": response.Content.Headers.ContentRange = new(0, 1, 3); break;
                case "range-total": response.Content.Headers.ContentRange = new(0, 2, 4); break;
                case "range-ignored": response.StatusCode = HttpStatusCode.OK; break;
                case "encoding": response.Content.Headers.ContentEncoding.Add("gzip"); break;
            }
            return response;
        });
        var pool = new ImportJobCoordinatorTests.TrackingPool();
        using var client = Client(handler, pool: pool);
        var error = await Assert.ThrowsAsync<S3ImportException>(() => client.ReadChunkAsync(source, Snapshot(source, 3), 0, null, CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(expected, error.Error);
        Assert.True(stream.Disposed);
        Assert.Equal(0, stream.Reads);
        Assert.Empty(pool.Rented);
    }

    [Theory]
    [InlineData("truncated", "source_protocol_error")]
    [InlineData("overlong", "source_protocol_error")]
    [InlineData("io", "source_unavailable")]
    public async Task InterruptedAndOverlongBodiesReleaseTheResponseAndEveryOwnedBuffer(string scenario, string expected)
    {
        var source = S3TestSupport.Source();
        byte[] bytes = Encoding.UTF8.GetBytes(scenario == "truncated" ? "{" : scenario == "overlong" ? "{}\nx" : "{}\n");
        var stream = new TrackingStream(bytes, fragment: 1) { Failure = scenario == "io" ? new IOException("private stream failure") : null };
        var handler = new Handler(_ => Response("{}\n", 3, stream: stream));
        var pool = new ImportJobCoordinatorTests.TrackingPool();
        using var client = Client(handler, pool: pool);
        var error = await Assert.ThrowsAsync<S3ImportException>(() => client.ReadChunkAsync(source, Snapshot(source, 3), 0, null, CancellationToken.None));
        Assert.Equal(502, error.StatusCode);
        Assert.Equal(expected, error.Error);
        Assert.Null(error.InnerException);
        Assert.True(stream.Disposed);
        Assert.NotEmpty(pool.Rented);
        Assert.Equal(pool.Rented.Count, pool.Returned.Count);
        Assert.All(pool.Returned, clear => Assert.True(clear));
    }

    [Fact]
    public async Task ARecordWithoutAnyBoundaryInsideTheChunkIsNotAdmitted()
    {
        var source = S3TestSupport.Source();
        var handler = new Handler(_ => Response("xxxxxxxx", 20));
        var pool = new ImportJobCoordinatorTests.TrackingPool();
        using var client = Client(handler, bytes: 8, pool: pool);
        var error = await Assert.ThrowsAsync<S3ImportException>(() => client.ReadChunkAsync(source, Snapshot(source, 20), 0, null, CancellationToken.None));
        Assert.Equal(413, error.StatusCode);
        Assert.Equal("source_record_too_large", error.Error);
        Assert.Equal(pool.Rented.Count, pool.Returned.Count);
        Assert.All(pool.Returned, clear => Assert.True(clear));
    }

    [Fact]
    public async Task IdentityEncodingAndAnExactFullBufferAreSupported()
    {
        var source = S3TestSupport.Source();
        var handler = new Handler(_ =>
        {
            var response = Response("abcdefgh", 8, version: null);
            response.Content.Headers.ContentEncoding.Add("IDENTITY");
            return response;
        });
        using var client = Client(handler, bytes: 8);
        using var chunk = await client.ReadChunkAsync(source, Snapshot(source, 8, null), 0, 8, CancellationToken.None);
        Assert.Equal("abcdefgh", Encoding.UTF8.GetString(chunk.Bytes.Span));
        Assert.Equal("bytes=0-7", Assert.Single(handler.Requests).Headers.Range!.ToString());
    }
}

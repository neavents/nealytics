using System.Net;
using Nealytics.Engine.Features.S3Import;
using static Nealytics.Engine.Tests.Unit.S3TransportTestSupport;

namespace Nealytics.Engine.Tests.Unit;

public sealed class S3MetadataTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("v")]
    [InlineData("null")]
    public async Task InspectionReturnsAProjectBoundSnapshotUsingSignedHeadAndDisposesTheResponse(string? version)
    {
        var source = S3TestSupport.Source();
        var stream = new TrackingStream([]);
        var handler = new Handler(_ =>
        {
            var response = Response(version: version, stream: stream);
            response.Content.Headers.ContentLength = 17;
            return response;
        });
        using var client = Client(handler);
        var snapshot = await client.InspectAsync(source, new("prefix/file.ndjson", version), CancellationToken.None);
        Assert.Equal(Snapshot(source, 17, version), snapshot);
        Assert.True(snapshot.IsValid(source));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Head, request.Method);
        Assert.Equal("20130524T000000Z", Assert.Single(request.Headers.GetValues("x-amz-date")));
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", Assert.Single(request.Headers.GetValues("Authorization")));
        Assert.Equal(version is null ? "" : "?versionId=" + version, request.RequestUri!.Query);
        Assert.True(stream.Disposed);
        Assert.Equal(0, stream.Reads);
        client.Dispose();
        Assert.True(handler.Disposed);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("version")]
    public async Task InvalidDescriptorsNeverReachTheTransport(string field)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<S3ImportException>(() => client.InspectAsync(S3TestSupport.Source(),
            new(field == "key" ? "" : "key", field == "version" ? "" : null), CancellationToken.None));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid_object_request", error.Error);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("tag", 502, "source_protocol_error")]
    [InlineData("weak", 502, "source_protocol_error")]
    [InlineData("tags", 502, "source_protocol_error")]
    [InlineData("version", 502, "source_protocol_error")]
    [InlineData("versions", 502, "source_protocol_error")]
    [InlineData("length", 502, "source_protocol_error")]
    [InlineData("size", 413, "source_too_large")]
    [InlineData("gzip", 415, "source_encoding_unsupported")]
    [InlineData("changed", 409, "source_changed")]
    public async Task InvalidMetadataIsRejectedBeforeAnyObjectRead(string scenario, int status, string expected)
    {
        var source = S3TestSupport.Source() with { MaxObjectBytes = 16 };
        var handler = new Handler(_ =>
        {
            var response = Response();
            switch (scenario)
            {
                case "tag": response.Headers.Remove("ETag"); break;
                case "weak": response.Headers.Remove("ETag"); response.Headers.TryAddWithoutValidation("ETag", "W/\"tag\""); break;
                case "tags": response.Headers.Remove("ETag"); response.Headers.TryAddWithoutValidation("ETag", new[] { "\"a\"", "\"b\"" }); break;
                case "version": response.Headers.Remove("x-amz-version-id"); response.Headers.TryAddWithoutValidation("x-amz-version-id", ""); break;
                case "versions": response.Headers.TryAddWithoutValidation("x-amz-version-id", "other"); break;
                case "length": response.Content = new UnknownLengthContent(); break;
                case "size": response.Content.Headers.ContentLength = 17; break;
                case "gzip": response.Content.Headers.ContentEncoding.Add("gzip"); break;
                case "changed": response.Headers.Remove("x-amz-version-id"); break;
            }
            return response;
        });
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<S3ImportException>(() => client.InspectAsync(source,
            new("prefix/file.ndjson", "v"), CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(expected, error.Error);
        Assert.Equal(expected, error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(404, 404, "source_object_not_found")]
    [InlineData(403, 502, "source_access_denied")]
    [InlineData(401, 502, "source_access_denied")]
    [InlineData(412, 409, "source_changed")]
    [InlineData(416, 409, "source_changed")]
    [InlineData(300, 502, "source_redirect_refused")]
    [InlineData(307, 502, "source_redirect_refused")]
    [InlineData(399, 502, "source_redirect_refused")]
    [InlineData(204, 502, "source_protocol_error")]
    [InlineData(299, 502, "source_protocol_error")]
    [InlineData(400, 502, "source_unavailable")]
    [InlineData(500, 502, "source_unavailable")]
    public async Task RemoteStatusesNeverExposePayloadsOrCredentials(int remote, int status, string expected)
    {
        var stream = new TrackingStream("private response body"u8.ToArray());
        var handler = new Handler(_ => new((HttpStatusCode)remote) { Content = new StreamContent(stream) });
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<S3ImportException>(() => client.InspectAsync(S3TestSupport.Source(), new("key", null), CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(expected, error.Error);
        Assert.True(stream.Disposed);
        Assert.Equal(0, stream.Reads);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("caller")]
    public async Task TransportFaultsAndCancellationHaveSeparateSafeOutcomes(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler(async (_, token) =>
        {
            if (failure == "http") throw new HttpRequestException("private connection detail");
            if (failure == "io") throw new IOException("private stream detail");
            if (failure == "caller") cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("must cancel");
        });
        using var client = Client(handler);
        var task = client.InspectAsync(S3TestSupport.Source() with { Timeout = TimeSpan.FromMilliseconds(20) }, new("key", null), cancellation.Token);
        if (failure == "caller") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else
        {
            var error = await Assert.ThrowsAsync<S3ImportException>(() => task.WaitAsync(TimeSpan.FromMilliseconds(500)));
            Assert.Equal(failure == "timeout" ? 504 : 502, error.StatusCode);
            Assert.Equal(failure == "timeout" ? "source_timeout" : "source_unavailable", error.Error);
            Assert.Null(error.InnerException);
        }
    }

    [Fact]
    public void DefaultTransportDisablesImplicitNetworkAndDecodingBehaviorsAndBoundsConnections()
    {
        using var handler = S3ObjectClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(1, handler.MaxConnectionsPerServer);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
        Assert.Equal(TimeSpan.FromSeconds(5), handler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), handler.PooledConnectionIdleTimeout);
    }

    [Fact]
    public async Task AnObjectExactlyAtTheConfiguredSourceSizeLimitIsInspectable()
    {
        var handler = new Handler(_ => { var response = Response(); response.Content.Headers.ContentLength = 16; return response; });
        using var client = Client(handler);
        var source = S3TestSupport.Source() with { MaxObjectBytes = 16 };
        Assert.Equal(16, (await client.InspectAsync(source, new("file", "v"), CancellationToken.None)).Bytes);
    }
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Nealytics.Engine.Features.S3Import;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed class S3ProtocolTests
{
    [Fact]
    public void PublicObjectModelsHaveAnExplicitSourceGeneratedContractWithoutConnectionSecrets()
    {
        var source = S3TestSupport.Source();
        var snapshot = S3TransportTestSupport.Snapshot(source, 7);
        string json = JsonSerializer.Serialize(snapshot, TelemetryAotContext.Default.S3ObjectSnapshot);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("\"tag\"", parsed.RootElement.GetProperty("etag").GetString());
        Assert.Equal("prefix/file.ndjson", parsed.RootElement.GetProperty("key").GetString());
        Assert.Equal("v", parsed.RootElement.GetProperty("versionId").GetString());
        Assert.Equal(7, parsed.RootElement.GetProperty("bytes").GetInt64());
        Assert.Equal(snapshot.SourceId, parsed.RootElement.GetProperty("sourceId").GetString());
        Assert.Equal(snapshot, JsonSerializer.Deserialize(json, TelemetryAotContext.Default.S3ObjectSnapshot));
        Assert.Equal(5, parsed.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain(source.AccessKey, json);
        Assert.DoesNotContain(source.SecretKey, json);
        Assert.DoesNotContain(source.Bucket, json);
        var request = new S3ObjectRequest("a", null);
        Assert.Equal(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request, TelemetryAotContext.Default.S3ObjectRequest),
            TelemetryAotContext.Default.S3ObjectRequest));
    }

    [Fact]
    public void SessionTokensConditionalHeadersAndVersionQueriesAreSignedAndCryptoBuffersAreReturnedCleared()
    {
        var source = S3TestSupport.Source() with { SessionToken = "token+/=" };
        using var request = new HttpRequestMessage(HttpMethod.Head, S3ObjectAddress.Create(source, "a", "v/+"));
        request.Headers.IfMatch.Add(new("\"tag\""));
        var pool = new ImportJobCoordinatorTests.TrackingPool();
        S3RequestSigner.Sign(request, source, new DateTimeOffset(2013, 5, 24, 3, 0, 0, TimeSpan.FromHours(3)), pool);
        Assert.Equal("token+/=", Assert.Single(request.Headers.GetValues("x-amz-security-token")));
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request,"
            + "SignedHeaders=host;if-match;x-amz-content-sha256;x-amz-date;x-amz-security-token,"
            + "Signature=06f752665537d5195f3755b282b3ecc747f77c7a60577c5e7e9f51fd0bd3e6a0",
            Assert.Single(request.Headers.GetValues("Authorization")));
        Assert.Single(pool.Rented);
        Assert.True(Assert.Single(pool.Returned));
    }

    [Theory]
    [InlineData("wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41")]
    [InlineData("!", "1d1a1aaa21c4ceb971b5c69fed2adb90d815c862fe25e5e1f3e37b403d598474")]
    public void SigningMatchesReferenceVectorsIncludingTheMinimumConfiguredSecret(string secret, string signature)
    {
        var source = S3TestSupport.Source() with { PathStyle = false, SecretKey = secret };
        using var request = new HttpRequestMessage(HttpMethod.Get, S3ObjectAddress.Create(source, "test.txt", null));
        request.Headers.Range = new RangeHeaderValue(0, 9);
        S3RequestSigner.Sign(request, source, new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("examplebucket.s3.amazonaws.com", request.Headers.Host);
        Assert.Equal("20130524T000000Z", Assert.Single(request.Headers.GetValues("x-amz-date")));
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request,"
            + "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date,Signature=" + signature,
            Assert.Single(request.Headers.GetValues("Authorization")));
        Assert.False(request.Headers.Contains("x-amz-security-token"));
    }

    [Theory]
    [InlineData(true, "https://s3.amazonaws.com/examplebucket/a//./../%25/%E6%97%A5%E6%9C%AC%E8%AA%9E%20%2B%3F%23", "/examplebucket/a//./../%25/%E6%97%A5%E6%9C%AC%E8%AA%9E%20%2B%3F%23")]
    [InlineData(false, "https://examplebucket.s3.amazonaws.com/a//./../%25/%E6%97%A5%E6%9C%AC%E8%AA%9E%20%2B%3F%23", "/a//./../%25/%E6%97%A5%E6%9C%AC%E8%AA%9E%20%2B%3F%23")]
    public void ObjectPathsPreserveLiteralSegmentsAndEncodeUtf8Exactly(bool pathStyle, string expected, string path)
    {
        var source = S3TestSupport.Source() with { PathStyle = pathStyle };
        var address = S3ObjectAddress.Create(source, "a//./../%/日本語 +?#", "+/x?= ");
        Assert.Equal(expected + "?versionId=%2B%2Fx%3F%3D%20", address.AbsoluteUri);
        Assert.Equal(path, address.AbsolutePath);
        Assert.Equal("?versionId=%2B%2Fx%3F%3D%20", address.Query);
    }

    [Fact]
    public void NondefaultPortsAndAllUnreservedCharactersHaveCanonicalAddresses()
    {
        var source = S3TestSupport.Source() with { Endpoint = new("https://store.example:9443"), PathStyle = false };
        Assert.Equal("https://examplebucket.store.example:9443/aAzZ09-_.~", S3ObjectAddress.Create(source, "aAzZ09-_.~", null).AbsoluteUri);
        source = source with { PathStyle = true, Endpoint = new("http://[::1]:9000") };
        Assert.Equal("http://[::1]:9000/examplebucket/a", S3ObjectAddress.Create(source, "a", null).AbsoluteUri);
        source = source with { Endpoint = new("https://täst.example") };
        Assert.Equal("https://xn--tst-qla.example/examplebucket/a", S3ObjectAddress.Create(source, "a", null).AbsoluteUri);
        Assert.Throws<EncoderFallbackException>(() => S3ObjectAddress.Encode("\ud800", true));
    }

    [Theory]
    [InlineData("", true, 2, 0)]
    [InlineData("a\nb\nc", false, 2, 4)]
    [InlineData("a\nb\nc", true, 2, 4)]
    [InlineData("a\nb", false, 2, 2)]
    [InlineData("a\nb", true, 2, 3)]
    [InlineData("a\r\n", false, 2, 3)]
    [InlineData("abc", false, 2, 0)]
    [InlineData("abc", true, 2, 3)]
    [InlineData("\n\n", true, 1, 1)]
    public void FramingRetainsOriginalBytesAndRespectsRecordAndSourceBoundaries(string body, bool final, int records, int expected)
    {
        Assert.Equal(expected, S3ChunkBoundary.Length(Encoding.UTF8.GetBytes(body), final, records));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("\"\"", false)]
    [InlineData("W/\"tag\"", false)]
    [InlineData("*", false)]
    [InlineData("\"a b\"", false)]
    [InlineData("\"a\"b\"", false)]
    [InlineData("\"a\r\n\"", false)]
    [InlineData("\"a\"", true)]
    [InlineData("\"a-2\"", true)]
    [InlineData("\"!\"", true)]
    [InlineData("\"~\"", true)]
    public void SnapshotsRequireOneBoundedStrongOpaqueTag(string? tag, bool expected)
    {
        Assert.Equal(expected, S3ObjectSnapshot.IsETag(tag));
        Assert.True(S3ObjectSnapshot.IsETag('"' + new string('a', 256) + '"'));
        Assert.False(S3ObjectSnapshot.IsETag('"' + new string('a', 257) + '"'));
        Assert.False(S3ObjectSnapshot.IsETag("\"日本語\""));
    }

    [Fact]
    public void SnapshotIdentityBindsResourceAndRepresentationWithoutCredentialMaterial()
    {
        var source = S3TestSupport.Source();
        string identity = S3ObjectSnapshot.Identity(source, "a", "v", "\"tag\"", 7);
        Assert.Equal("F3DC469A571201F439A7180CF7ABA7D302E7EAD8B0FB8335F5E97C567F2ECCDF", identity);
        var snapshot = new S3ObjectSnapshot("a", "v", "\"tag\"", 7, identity);
        Assert.True(snapshot.IsValid(source));
        Assert.False((snapshot with { Key = "b" }).IsValid(source));
        Assert.False((snapshot with { VersionId = "w" }).IsValid(source));
        Assert.False((snapshot with { ETag = "\"new\"" }).IsValid(source));
        Assert.False((snapshot with { Bytes = 8 }).IsValid(source));
        Assert.False((snapshot with { Bytes = -1 }).IsValid(source));
        Assert.False((snapshot with { Bytes = source.MaxObjectBytes + 1 }).IsValid(source));
        Assert.False((snapshot with { SourceId = "wrong" }).IsValid(source));
        Assert.True(snapshot.IsValid(source with { AccessKey = "rotated", SecretKey = "rotated", SessionToken = "rotated" }));
        foreach (var changed in new[]
        {
            source with { Name = "other" }, source with { ProjectId = "other" }, source with { Endpoint = new("https://other.example") },
            source with { Bucket = "other" }, source with { PathStyle = false }
        }) Assert.False(snapshot.IsValid(changed));
    }

    [Fact]
    public void ACallerComputedDigestCannotAuthorizeStructurallyInvalidObjectMetadata()
    {
        var source = S3TestSupport.Source() with { Prefix = "prefix/", MaxObjectBytes = 7 };
        var valid = S3TransportTestSupport.Snapshot(source, 7);
        Assert.True(valid.IsValid(source));
        foreach (var invalid in new[]
        {
            valid with { Key = "other/file" }, valid with { Key = "" }, valid with { VersionId = "" },
            valid with { ETag = "W/\"tag\"" }, valid with { Bytes = -1 }, valid with { Bytes = 8 }
        })
        {
            var signed = invalid with
            {
                SourceId = S3ObjectSnapshot.Identity(source, invalid.Key, invalid.VersionId, invalid.ETag, invalid.Bytes)
            };
            Assert.False(signed.IsValid(source));
        }
    }
}

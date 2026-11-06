using System.Text;
using System.Text.Json;
using Nealytics.Engine.Features.GcsImport;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using static Nealytics.Engine.Tests.Unit.GcsTestSupport;

namespace Nealytics.Engine.Tests.Unit;

public sealed class GcsSourceTests
{
    [Fact]
    public void SourcesAreDisabledByDefaultAndCompileFrozenProjectScopedDeclarations()
    {
        var defaults = new GcsSourceOptions();
        Assert.Empty(new TelemetryEngineOptions().GcsSources);
        Assert.Equal("https://storage.googleapis.com", defaults.Endpoint);
        Assert.Empty(defaults.Name); Assert.Empty(defaults.ProjectId); Assert.Empty(defaults.Bucket);
        Assert.Empty(defaults.Prefix); Assert.Empty(defaults.AccessKey); Assert.Empty(defaults.SecretKey);
        Assert.Empty(defaults.IdentityKey); Assert.False(defaults.AllowInsecureHttp);
        Assert.Equal(30, defaults.RequestTimeoutSeconds); Assert.Equal(1_099_511_627_776, defaults.MaxObjectBytes);
        var declaration = Options(); using var catalog = Catalog(declaration);
        var source = catalog.Find("source", "p")!;
        declaration.Name = "changed"; declaration.Bucket = "changed"; declaration.AccessKey = "changed";
        declaration.SecretKey = "changed"; declaration.Prefix = "changed"; declaration.RequestTimeoutSeconds = 1;
        Assert.Equal("source", source.Name); Assert.Equal("private-bucket", source.Bucket);
        Assert.Equal("GOOGTESTACCESS", source.AccessKey); Assert.Equal("private-secret+/=", source.SecretKey);
        Assert.Empty(source.Prefix); Assert.Equal(TimeSpan.FromSeconds(30), source.Timeout);
        Assert.Equal(1, catalog.Count); Assert.Null(catalog.Find("Source", "p"));
        Assert.Null(catalog.Find("source", "P")); Assert.Null(catalog.Find("missing", "p"));
    }

    [Fact]
    public void PrivateIdentitiesHaveAnIndependentVectorStableRestartAndCredentialRotation()
    {
        using var first = Catalog(); using var restarted = Catalog();
        var source = first.Find("source", "p")!; var snapshot = Snapshot(source, 7);
        Assert.Equal("EAC089D78F879331DCD62F94D0673DCBD194906729DB4374AC9B09E4BD12345F", snapshot.SourceId);
        Assert.True(snapshot.IsValid(source)); Assert.True(snapshot.IsValid(restarted.Find("source", "p")!));
        var rotated = Options(); rotated.AccessKey = "OTHERACCESS"; rotated.SecretKey = "other-secret";
        using var other = Catalog(rotated); Assert.True(snapshot.IsValid(other.Find("source", "p")!));
        string json = JsonSerializer.Serialize(snapshot, TelemetryAotContext.Default.GcsObjectSnapshot);
        Assert.DoesNotContain(source.Bucket, json); Assert.DoesNotContain(source.Endpoint.Host, json);
        Assert.DoesNotContain(source.AccessKey, json); Assert.DoesNotContain(source.SecretKey, json);
        Assert.DoesNotContain(Options().IdentityKey, json);
        Assert.Equal(snapshot, JsonSerializer.Deserialize(json, TelemetryAotContext.Default.GcsObjectSnapshot));
        var request = new GcsObjectRequest("key", null);
        Assert.Equal(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request,
            TelemetryAotContext.Default.GcsObjectRequest), TelemetryAotContext.Default.GcsObjectRequest));
    }

    [Fact]
    public void SourceAndAccessTokensAcceptTheDeclaredAsciiPunctuationOnly()
    {
        var options = Options(); options.Name = "a-b_c"; options.AccessKey = "GOOG-X_Y";
        using (var catalog = Catalog(options)) Assert.NotNull(catalog.Find("a-b_c", "p"));
        options.Name = "K"; Assert.Throws<InvalidOperationException>(() => Catalog(options));
        options.Name = null!; Assert.Throws<InvalidOperationException>(() => Catalog(options));
        options.Name = ""; Assert.Throws<InvalidOperationException>(() => Catalog(options));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("project")]
    [InlineData("endpoint")]
    [InlineData("bucket")]
    [InlineData("prefix")]
    [InlineData("identity-key")]
    public void ResourceOrIdentityKeyChangesFenceOldDescriptors(string change)
    {
        using var before = Catalog(); var options = Options();
        switch (change)
        {
            case "name": options.Name = "other"; break;
            case "project": options.ProjectId = "other"; break;
            case "endpoint": options.Endpoint = "https://other.example"; break;
            case "bucket": options.Bucket = "other-bucket"; break;
            case "prefix": options.Prefix = "prefix/"; break;
            case "identity-key": options.IdentityKey = Convert.ToBase64String(new byte[32]); break;
        }
        using var after = Catalog(options);
        Assert.False(Snapshot(before.Find("source", "p")!, 7).IsValid(after.Find(options.Name, options.ProjectId)!));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("generation")]
    [InlineData("metageneration")]
    [InlineData("etag")]
    [InlineData("bytes")]
    [InlineData("source")]
    public void DescriptorTamperingNeverValidates(string change)
    {
        using var source = Source(); var snapshot = Snapshot(source, 7);
        snapshot = change switch
        {
            "key" => snapshot with { Key = "other" }, "generation" => snapshot with { Generation = 18 },
            "metageneration" => snapshot with { Metageneration = 4 }, "etag" => snapshot with { ETag = "\"other\"" },
            "bytes" => snapshot with { Bytes = 8 }, _ => snapshot with { SourceId = new string('é', 64) }
        };
        Assert.False(snapshot.IsValid(source));
    }

    [Theory]
    [InlineData("name", 1, true)]
    [InlineData("name", 64, true)]
    [InlineData("name", 65, false)]
    [InlineData("project", 256, true)]
    [InlineData("project", 257, false)]
    [InlineData("access", 128, true)]
    [InlineData("access", 129, false)]
    [InlineData("secret", 0, false)]
    [InlineData("secret", 1, true)]
    [InlineData("secret", 256, true)]
    [InlineData("secret", 257, false)]
    [InlineData("prefix", 1024, true)]
    [InlineData("prefix", 1025, false)]
    [InlineData("timeout", 1, true)]
    [InlineData("timeout", 120, true)]
    [InlineData("timeout", 0, false)]
    [InlineData("timeout", 121, false)]
    [InlineData("bytes", 1, true)]
    [InlineData("bytes", 0, false)]
    public void EveryDeclarationBoundaryIsInclusiveAndBounded(string field, int bound, bool valid)
    {
        var options = Options();
        switch (field)
        {
            case "name": options.Name = new string('n', bound); break;
            case "project": options.ProjectId = new string('p', bound); break;
            case "access": options.AccessKey = new string('a', bound); break;
            case "secret": options.SecretKey = new string('!', bound); break;
            case "prefix": options.Prefix = new string('x', bound); break;
            case "timeout": options.RequestTimeoutSeconds = bound; break;
            case "bytes": options.MaxObjectBytes = bound; break;
        }
        if (!valid) Assert.Throws<InvalidOperationException>(() => Catalog(options));
        else { using var catalog = Catalog(options); Assert.NotNull(catalog.Find(options.Name, options.ProjectId)); }
    }

    [Theory]
    [InlineData("name")]
    [InlineData("project")]
    [InlineData("endpoint")]
    [InlineData("long-endpoint")]
    [InlineData("bad-endpoint")]
    [InlineData("bad-idn")]
    [InlineData("null-prefix")]
    [InlineData("path")]
    [InlineData("query")]
    [InlineData("userinfo")]
    [InlineData("fragment")]
    [InlineData("scheme")]
    [InlineData("http")]
    [InlineData("access")]
    [InlineData("secret")]
    [InlineData("prefix")]
    [InlineData("bad-prefix")]
    [InlineData("key")]
    [InlineData("short-key")]
    [InlineData("null-key")]
    public void MalformedSettingsFailBeforeUseWithAFixedSecretSafeMessage(string field)
    {
        var options = Options();
        switch (field)
        {
            case "name": options.Name = "bad name"; break;
            case "project": options.ProjectId = null!; break;
            case "endpoint": options.Endpoint = null!; break;
            case "bad-endpoint": options.Endpoint = "private-invalid-uri"; break;
            case "bad-idn": options.Endpoint = "https://\u200D.example"; break;
            case "null-prefix": options.Prefix = null!; break;
            case "long-endpoint": options.Endpoint = "https://" + new string('a', 4096) + ".example"; break;
            case "path": options.Endpoint += "/not-root"; break;
            case "query": options.Endpoint += "?secret=private"; break;
            case "userinfo": options.Endpoint = "https://secret:private@origin.example"; break;
            case "fragment": options.Endpoint += "#private"; break;
            case "scheme": options.Endpoint = "ftp://origin.example"; break;
            case "http": options.Endpoint = "http://localhost"; break;
            case "access": options.AccessKey = "bad+key"; break;
            case "secret": options.SecretKey = "bad\nsecret"; break;
            case "prefix": options.Prefix = "bad\rprefix"; break;
            case "bad-prefix": options.Prefix = "\ud800"; break;
            case "key": options.IdentityKey = new string('!', 44); break;
            case "short-key": options.IdentityKey = Convert.ToBase64String(new byte[31]); break;
            case "null-key": options.IdentityKey = null!; break;
        }
        var failure = Assert.Throws<InvalidOperationException>(() => Catalog(options));
        Assert.Equal("GCS source settings are invalid or conflict with a declared source.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("z09", true)]
    [InlineData("1.a.3.4", true)]
    [InlineData("123", true)]
    [InlineData("a_b", true)]
    [InlineData("a.b.c", true)]
    [InlineData(null, false)]
    [InlineData("ab", false)]
    [InlineData("Abc", false)]
    [InlineData("a b", false)]
    [InlineData(".ab", false)]
    [InlineData("ab-", false)]
    [InlineData("a..b", false)]
    [InlineData("192.168.5.4", false)]
    [InlineData("a/b", false)]
    public void BucketSyntaxUsesGcsComponentsAndPathStyleWithoutAcceptingIpAddresses(string? bucket, bool valid)
    {
        var options = Options(); options.Bucket = bucket!;
        if (!valid) Assert.Throws<InvalidOperationException>(() => Catalog(options));
        else { using var catalog = Catalog(options); Assert.Equal(bucket, catalog.Find("source", "p")!.Bucket); }
    }

    [Fact]
    public void MaximumDottedBucketsAndTheirComponentLimitsAreSeparate()
    {
        var options = Options(); options.Bucket = new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 30);
        using (var sources = Catalog(options)) Assert.Equal(222, sources.Find("source", "p")!.Bucket.Length);
        options.Bucket += "d"; Assert.Throws<InvalidOperationException>(() => Catalog(options));
        options.Bucket = new string('a', 64); Assert.Throws<InvalidOperationException>(() => Catalog(options));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData(".well-known/acme-challenge/key", false)]
    [InlineData("bad\nkey", false)]
    [InlineData("bad\rkey", false)]
    [InlineData("a//./../日本語 +%?#", true)]
    public void KeysAreLiteralProviderNamesWithinTheConfiguredPrefix(string? key, bool valid)
    {
        using var source = Source(); Assert.Equal(valid, GcsSources.Key(source, key));
        var options = Options(); options.Prefix = "private/"; using var catalog = Catalog(options);
        Assert.False(GcsSources.Key(catalog.Find("source", "p")!, "other/key"));
        Assert.True(GcsSources.Key(catalog.Find("source", "p")!, "private/key"));
    }

    [Fact]
    public void Utf8LimitsAndAddressesPreserveWireBytesAndGenerationSelection()
    {
        var options = Options(); options.Endpoint = "http://[::1]:9000"; options.AllowInsecureHttp = true;
        using var catalog = Catalog(options); var source = catalog.Find("source", "p")!;
        Assert.True(GcsSources.Key(source, new string('日', 341) + "x"));
        Assert.False(GcsSources.Key(source, new string('日', 342)));
        Assert.False(GcsSources.Key(source, "\ud800"));
        var address = GcsObjectAddress.Create(source, "a//./../%/日本語 +?#", 17);
        Assert.Equal("http://[::1]:9000/private-bucket/a//./../%25/%E6%97%A5%E6%9C%AC%E8%AA%9E%20%2B%3F%23?generation=17", address.AbsoluteUri);
        Assert.Equal("?generation=17", address.Query);
        Assert.Equal("", GcsObjectAddress.Create(source, "aAzZ09-_.~", null).Query);
        options.Endpoint = "https://täst.example";
        using var idn = Catalog(options);
        Assert.StartsWith("https://xn--tst-qla.example/", GcsObjectAddress.Create(idn.Find("source", "p")!, "x", null).AbsoluteUri);
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("null-source")]
    [InlineData("duplicate")]
    [InlineData("project")]
    [InlineData("ndjson")]
    [InlineData("jobs")]
    public void CatalogPrerequisitesProjectMembershipAndUniquenessFailClosed(string fault)
    {
        Assert.Throws<InvalidOperationException>(() => Catalog(customize: settings =>
        {
            switch (fault)
            {
                case "null-list": settings.GcsSources = null!; break;
                case "null-source": settings.GcsSources.Add(null!); break;
                case "duplicate": settings.GcsSources.Add(Options()); break;
                case "project": settings.ProjectSchemas = [new() { ProjectId = "other" }]; break;
                case "ndjson": settings.EnableNdjsonImport = false; break;
                case "jobs": settings.EnableImportJobs = false; break;
            }
        }));
    }

    [Fact]
    public void EmptyAndFullCatalogsRemainBoundedAndKeysAreClearedOnEveryOwnershipExit()
    {
        var options = new TelemetryEngineOptions { EnableNdjsonImport = true, EnableImportJobs = true };
        using (var empty = new GcsSources(options, new ProjectCatalog(options, ProjectContext.Compile("p", options)))) Assert.Equal(0, empty.Count);
        for (int i = 0; i < 32; i++) { var declaration = Options(); declaration.Name += i; options.GcsSources.Add(declaration); }
        using (var full = new GcsSources(options, new ProjectCatalog(options, ProjectContext.Compile("p", options)))) Assert.Equal(32, full.Count);
        options.GcsSources.Add(Options()); Assert.Throws<InvalidOperationException>(() => new GcsSources(options, new ProjectCatalog(options, ProjectContext.Compile("p", options))));
        byte[] owned = Enumerable.Repeat((byte)19, 32).ToArray();
        var source = new GcsSource(Options(), new Uri("https://origin.example"), owned);
        source.Dispose(); source.Dispose(); Assert.All(owned, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => source.Identity("key", 1, 1, "\"tag\"", 0));
        byte[] failed = Enumerable.Repeat((byte)17, 32).ToArray(); var tooLarge = Options(); tooLarge.Name = new string('x', 300000);
        Assert.ThrowsAny<Exception>(() => new GcsSource(tooLarge, new Uri("https://origin.example"), failed));
        Assert.All(failed, value => Assert.Equal(0, value));
        byte[] decoding = Enumerable.Repeat((byte)13, 32).ToArray();
        Assert.Throws<InvalidOperationException>(() => GcsSources.DecodeOwnedKey(new string('!', 44), decoding));
        Assert.All(decoding, value => Assert.Equal(0, value));
    }

    [Fact]
    public void PartialCatalogFailureClearsSuccessfullyCompiledKeys()
    {
        var options = new TelemetryEngineOptions { EnableNdjsonImport = true, EnableImportJobs = true, GcsSources = [Options(), Options()] };
        List<byte[]> keys = [];
        Assert.Throws<InvalidOperationException>(() => new GcsSources(options, new ProjectCatalog(options,
            ProjectContext.Compile("p", options)), value => { var key = Convert.FromBase64String(value!); keys.Add(key); return key; }));
        Assert.All(Assert.Single(keys), value => Assert.Equal(0, value));
    }

    [Fact]
    public void StructuralValidationRemainsIndependentOfACorrectPrivateMac()
    {
        var options = Options(); options.Prefix = "private/";
        using var catalog = Catalog(options); var source = catalog.Find("source", "p")!;
        foreach (var snapshot in new[]
        {
            Snapshot(source, 7, "outside/key"), Snapshot(source, 7, "private/key", generation: 0),
            Snapshot(source, 7, "private/key", metageneration: 0), Snapshot(source, -1, "private/key"),
            Snapshot(source, source.MaxObjectBytes + 1, "private/key"), Snapshot(source, 7, "private/key", tag: "W/\"tag\"")
        }) Assert.False(snapshot.IsValid(source));
    }

    [Fact]
    public void CatalogDisposalClearsAllOwnedKeysAndInvalidatesEveryCompiledSource()
    {
        var other = Options(); other.Name = "other";
        var settings = new TelemetryEngineOptions { EnableNdjsonImport = true, EnableImportJobs = true, GcsSources = [Options(), other] };
        List<byte[]> owned = [];
        using var catalog = new GcsSources(settings, new ProjectCatalog(settings, ProjectContext.Compile("p", settings)), value =>
        {
            byte[] key = Convert.FromBase64String(value!); owned.Add(key); return key;
        });
        var sources = new[] { catalog.Find("source", "p")!, catalog.Find("other", "p")! };
        catalog.Dispose(); catalog.Dispose(); Assert.Equal(2, owned.Count);
        Assert.All(owned, key => Assert.All(key, value => Assert.Equal(0, value)));
        Assert.All(sources, source => Assert.Throws<ObjectDisposedException>(() => source.Identity("key", 17, 3, "\"tag\"", 7)));
    }

    [Fact]
    public void MaximalEscapedConfigurationFitsWithoutAnExtraEncodingReservation()
    {
        var options = Options(); options.Name = new string('n', 64); options.ProjectId = new string('&', 256);
        options.Prefix = new string('&', 1024); options.SecretKey = "~";
        options.Endpoint = "https://" + string.Join('.', Enumerable.Repeat(new string('é', 10), 5));
        options.Bucket = new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 30);
        using var catalog = Catalog(options); var source = catalog.Find(options.Name, options.ProjectId)!;
        Assert.True(Snapshot(source, 7, options.Prefix).IsValid(source));
        Assert.Equal("~", source.SecretKey);
    }

    [Fact]
    public void GenerationJsonPreservesInt64PrecisionForClientsWithoutExactLargeNumbers()
    {
        using var source = Source(); var snapshot = Snapshot(source, 7, generation: long.MaxValue, metageneration: long.MaxValue);
        string json = JsonSerializer.Serialize(snapshot, TelemetryAotContext.Default.GcsObjectSnapshot);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("9223372036854775807", parsed.RootElement.GetProperty("generation").GetString());
        Assert.Equal("9223372036854775807", parsed.RootElement.GetProperty("metageneration").GetString());
        Assert.Equal(snapshot, JsonSerializer.Deserialize(json, TelemetryAotContext.Default.GcsObjectSnapshot));
        Assert.Equal(new GcsObjectRequest("key", 17), JsonSerializer.Deserialize("{\"key\":\"key\",\"generation\":\"17\"}", TelemetryAotContext.Default.GcsObjectRequest));
        Assert.Equal(new GcsObjectRequest("key", 17), JsonSerializer.Deserialize("{\"key\":\"key\",\"generation\":17}", TelemetryAotContext.Default.GcsObjectRequest));
    }

    [Fact]
    public void MaximalEscapedDescriptorsFitAndMalformedBoundariesFailBeforeMacComputation()
    {
        using var source = Source(); string key = new string('&', 1024); string tag = "\"" + new string('&', 1022) + "\"";
        Assert.True(Snapshot(source, source.MaxObjectBytes, key, long.MaxValue, long.MaxValue, tag).IsValid(source));
        var valid = Snapshot(source, 7);
        foreach (var bad in new[] { valid with { Generation = 0 }, valid with { Metageneration = 0 }, valid with { Bytes = -1 },
            valid with { Bytes = source.MaxObjectBytes + 1 }, valid with { ETag = null! }, valid with { SourceId = null! }, valid with { SourceId = "short" },
            valid with { ETag = "W/\"tag\"" }, valid with { ETag = " \"tag\"" }, valid with { ETag = new string('x', 1025) } }) Assert.False(bad.IsValid(source));
    }
}

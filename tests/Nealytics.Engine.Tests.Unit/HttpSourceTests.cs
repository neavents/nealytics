using System.Collections.Frozen;
using System.Text.Json;
using Nealytics.Engine.Features.HttpImport;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using static Nealytics.Engine.Tests.Unit.HttpObjectTestSupport;

namespace Nealytics.Engine.Tests.Unit;

public sealed class HttpSourceTests
{
    [Fact]
    public void DefaultSourcesAreDisabledAndCompiledSettingsAreFrozenAndProjectScoped()
    {
        var defaults = new HttpSourceOptions();
        Assert.Empty(defaults.Name); Assert.Empty(defaults.ProjectId); Assert.Empty(defaults.Url);
        Assert.Empty(defaults.IdentityKey); Assert.Empty(defaults.Headers);
        Assert.False(defaults.AllowInsecureHttp); Assert.Equal(30, defaults.RequestTimeoutSeconds);
        Assert.Equal(1_099_511_627_776, defaults.MaxObjectBytes);
        var declaration = Options();
        using var catalog = Catalog(declaration);
        var source = catalog.Find("source", "p")!;
        declaration.Headers["Authorization"] = "changed"; declaration.Url = "https://changed.example";
        declaration.IdentityKey = "changed"; declaration.Name = "changed";
        Assert.Equal("source", source.Name);
        Assert.Equal("https://origin.example/file.ndjson", source.Url.AbsoluteUri);
        Assert.Equal("Bearer private", source.Headers["authorization"]);
        Assert.Equal(1, catalog.Count); Assert.Null(catalog.Find("Source", "p"));
        Assert.Null(catalog.Find("source", "P")); Assert.Null(catalog.Find("missing", "p"));
        catalog.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.Identity("\"tag\"", 17));
    }

    [Fact]
    public void StableRestartIdentityHasAnIndependentHmacVectorAndContainsNoLocatorSecrets()
    {
        using var first = Catalog(); using var restarted = Catalog();
        var source = first.Find("source", "p")!;
        string identity = source.Identity("\"tag\"", 17);
        Assert.Equal("829B08659ABA0FD3620BBDE0ECE966316E122CB4809B54470B7C3640F8403075", identity);
        Assert.Equal(identity, restarted.Find("source", "p")!.Identity("\"tag\"", 17));
        var snapshot = Snapshot(source, 17);
        Assert.True(snapshot.IsValid(source));
        string json = JsonSerializer.Serialize(snapshot, TelemetryAotContext.Default.HttpObjectSnapshot);
        Assert.DoesNotContain("origin.example", json); Assert.DoesNotContain("Bearer", json);
        Assert.DoesNotContain("private", json); Assert.DoesNotContain(Options().IdentityKey, json);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("query")]
    [InlineData("header")]
    [InlineData("key")]
    [InlineData("name")]
    [InlineData("project")]
    public void ResourceSelectionOrKeyChangesFenceOldSnapshots(string field)
    {
        using var original = Catalog(); var declaration = Options();
        switch (field)
        {
            case "url": declaration.Url += "/other"; break;
            case "query": declaration.Url += "?private=selected"; break;
            case "header": declaration.Headers["Authorization"] = "other"; break;
            case "key": declaration.IdentityKey = Convert.ToBase64String(new byte[32]); break;
            case "name": declaration.Name = "other"; break;
            case "project": declaration.ProjectId = "other"; break;
        }
        using var changed = Catalog(declaration);
        var selected = changed.Find(declaration.Name, declaration.ProjectId)!;
        Assert.False(Snapshot(original.Find("source", "p")!, 17).IsValid(selected));
    }

    [Fact]
    public void HeaderOrderAndCasingDoNotChangeSelectionButOpaqueBytesAndLengthDo()
    {
        var a = Options(); a.Headers["X-Selection"] = "one";
        var b = Options(); b.Headers = new() { ["x-selection"] = "one", ["authorization"] = "Bearer private" };
        using var left = Catalog(a); using var right = Catalog(b);
        var source = left.Find("source", "p")!; var other = right.Find("source", "p")!;
        Assert.Equal("494EC5A7514FB7EC9DE609AC2526FB0E694C9758B0F83DE059B996DD66A9A9AF", source.Identity("\"tag\"", 17));
        Assert.Equal(source.Identity("\"tag\"", 17), other.Identity("\"tag\"", 17));
        Assert.NotEqual(source.Identity("\"tag\"", 17), source.Identity("\"Tag\"", 17));
        Assert.NotEqual(source.Identity("\"tag\"", 17), source.Identity("\"tag\"", 18));
    }

    [Fact]
    public void SourceDisposalClearsItsOwnedKeyAndConstructionFailureAlsoClearsIt()
    {
        byte[] key = Enumerable.Repeat((byte)17, 32).ToArray();
        var source = new HttpSource("name", "p", new Uri("https://origin.example"),
            new Dictionary<string, string>().ToFrozenDictionary(), key, 100, TimeSpan.FromSeconds(1));
        source.Dispose(); source.Dispose(); Assert.All(key, value => Assert.Equal(0, value));
        byte[] failedKey = Enumerable.Repeat((byte)19, 32).ToArray();
        Assert.ThrowsAny<Exception>(() => new HttpSource(new string('x', 300_000), "p", new Uri("https://origin.example"),
            new Dictionary<string, string>().ToFrozenDictionary(), failedKey, 100, TimeSpan.FromSeconds(1)));
        Assert.All(failedKey, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("project")]
    [InlineData("url")]
    [InlineData("long-url")]
    [InlineData("userinfo")]
    [InlineData("fragment")]
    [InlineData("scheme")]
    [InlineData("http")]
    [InlineData("size")]
    [InlineData("timeout")]
    [InlineData("key")]
    [InlineData("short-key")]
    [InlineData("null-key")]
    [InlineData("null-headers")]
    public void InvalidDeclarationsFailWithFixedSecretSafeDiagnostics(string invalid)
    {
        var options = Options();
        switch (invalid)
        {
            case "name": options.Name = "bad name"; break;
            case "project": options.ProjectId = " "; break;
            case "url": options.Url = "private-invalid-uri"; break;
            case "long-url": options.Url = "https://origin.example/" + new string('x', 16384); break;
            case "userinfo": options.Url = "https://secret:private@origin.example"; break;
            case "fragment": options.Url += "#secret"; break;
            case "scheme": options.Url = "ftp://origin.example/file"; break;
            case "http": options.Url = "http://origin.example/file"; break;
            case "size": options.MaxObjectBytes = 0; break;
            case "timeout": options.RequestTimeoutSeconds = 121; break;
            case "key": options.IdentityKey = new string('!', 44); break;
            case "short-key": options.IdentityKey = Convert.ToBase64String(new byte[31]); break;
            case "null-key": options.IdentityKey = null!; break;
            case "null-headers": options.Headers = null!; break;
        }
        var exception = Assert.Throws<InvalidOperationException>(() => Catalog(options));
        Assert.Equal("HTTP source settings are invalid or conflict with a declared source.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Range")]
    [InlineData("If-Match")]
    [InlineData("IF-None-Match")]
    [InlineData("Accept-Encoding")]
    [InlineData("Content-Type")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("Keep-Alive")]
    [InlineData("Upgrade")]
    [InlineData("Trailer")]
    [InlineData("TE")]
    [InlineData("Expect")]
    [InlineData("X Bad")]
    [InlineData("")]
    public void FramingConditionalAndInvalidHeadersCannotBeInjected(string name)
    {
        var options = Options(); options.Headers[name] = "private";
        Assert.Throws<InvalidOperationException>(() => Catalog(options));
    }

    [Theory]
    [InlineData("bad\r\nsmuggled: value")]
    [InlineData("bad\tvalue")]
    [InlineData("bad\0value")]
    [InlineData("日本語")]
    [InlineData(null)]
    public void HeaderValuesCannotCarryControlsOrNonAsciiWireText(string? value)
    {
        var options = Options(); options.Headers["X-Data"] = value!;
        Assert.Throws<InvalidOperationException>(() => Catalog(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void CatalogSupportsEmptyAndMaximumSourceCounts(int count)
    {
        var options = new TelemetryEngineOptions { EnableNdjsonImport = true, EnableImportJobs = true };
        for (int i = 0; i < count; i++) { var source = Options(); source.Name = "source" + i; options.HttpSources.Add(source); }
        using var catalog = new HttpSources(options, new ProjectCatalog(options, ProjectContext.Compile("baseline", options)));
        Assert.Equal(count, catalog.Count);
        options.HttpSources.AddRange(Enumerable.Range(0, 33).Select(_ => Options()));
        Assert.Throws<InvalidOperationException>(() => new HttpSources(options, new ProjectCatalog(options, ProjectContext.Compile("baseline", options))));
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("null-source")]
    [InlineData("duplicate")]
    [InlineData("unknown-project")]
    [InlineData("ndjson")]
    [InlineData("jobs")]
    public void SourceCatalogCannotBypassConfiguredProjectsPrerequisitesOrUniqueness(string invalid)
    {
        Assert.Throws<InvalidOperationException>(() => Catalog(customize: options =>
        {
            switch (invalid)
            {
                case "null-list": options.HttpSources = null!; break;
                case "null-source": options.HttpSources.Add(null!); break;
                case "duplicate": options.HttpSources.Add(Options()); break;
                case "unknown-project": options.ProjectSchemas = [new() { ProjectId = "other" }]; break;
                case "ndjson": options.EnableNdjsonImport = false; break;
                case "jobs": options.EnableImportJobs = false; break;
            }
        }));
    }

    [Theory]
    [InlineData("name", 64, true)]
    [InlineData("name", 65, false)]
    [InlineData("project", 256, true)]
    [InlineData("project", 257, false)]
    [InlineData("timeout", 1, true)]
    [InlineData("timeout", 120, true)]
    [InlineData("timeout", 0, false)]
    [InlineData("header-name", 1, true)]
    [InlineData("header-name", 128, true)]
    [InlineData("header-name", 129, false)]
    [InlineData("header-value", 4096, true)]
    [InlineData("header-value", 4097, false)]
    public void EveryDeclaredBoundaryIsInclusiveAndIndependentlyBounded(string field, int limit, bool valid)
    {
        var options = Options();
        switch (field)
        {
            case "name": options.Name = new string('n', limit); break;
            case "project": options.ProjectId = new string('p', limit); break;
            case "timeout": options.RequestTimeoutSeconds = limit; break;
            case "header-name": options.Headers[new string('a', limit)] = "x"; break;
            case "header-value": options.Headers["X-Value"] = new string('x', limit); break;
        }
        if (!valid) Assert.Throws<InvalidOperationException>(() => Catalog(options));
        else { using var sources = Catalog(options); Assert.NotNull(sources.Find(options.Name, options.ProjectId)); }
    }

    [Fact]
    public void HeaderAggregateAndCountLimitsAreSeparateFromTheSingleValueCap()
    {
        var options = Options(); options.Headers.Clear();
        for (int i = 0; i < 32; i++) options.Headers["x-" + i] = "";
        using (var sources = Catalog(options)) Assert.Equal(32, sources.Find("source", "p")!.Headers.Count);
        options.Headers["extra"] = ""; Assert.Throws<InvalidOperationException>(() => Catalog(options));
        options.Headers = new() { ["x-one"] = new string('x', 4096), ["x-two"] = new string('x', 4096),
            ["x-three"] = new string('x', 4096), ["x-four"] = new string('x', 4096) };
        Assert.Throws<InvalidOperationException>(() => Catalog(options));
        options.Headers = new(StringComparer.Ordinal) { ["X-Key"] = "one", ["x-key"] = "two" };
        Assert.Throws<InvalidOperationException>(() => Catalog(options));
    }

    [Fact]
    public void LargeEscapedConfigurationFitsItsOwnBoundAndSnapshotsValidateStructureBeforeTheirMac()
    {
        var options = Options(); options.Url = "https://origin.example/?" + new string('&', 16000);
        options.Headers = new() { ["X-One"] = new string('&', 4096), ["X-Two"] = new string('&', 4096), ["X-Three"] = new string('&', 4096) };
        using var sources = Catalog(options); var source = sources.Find("source", "p")!;
        Assert.True(Snapshot(source, 1).IsValid(source));
        foreach (var snapshot in new[] { new HttpObjectSnapshot("invalid", 1, source.Identity("invalid", 1)),
            new HttpObjectSnapshot("\"tag\"", -1, source.Identity("\"tag\"", -1)),
            new HttpObjectSnapshot("\"tag\"", source.MaxObjectBytes + 1, source.Identity("\"tag\"", source.MaxObjectBytes + 1)),
            Snapshot(source, 1) with { SourceId = null! }, Snapshot(source, 1) with { SourceId = new string('é', 64) } })
            Assert.False(snapshot.IsValid(source));
    }

    [Fact]
    public void FailedCatalogConstructionClearsKeysOfAlreadyCompiledSources()
    {
        var options = new TelemetryEngineOptions { EnableNdjsonImport = true, EnableImportJobs = true,
            HttpSources = [Options(), Options()] };
        List<byte[]> allocated = [];
        Assert.Throws<InvalidOperationException>(() => new HttpSources(options,
            new ProjectCatalog(options, ProjectContext.Compile("baseline", options)), encoded =>
            {
                var key = Convert.FromBase64String(encoded!); allocated.Add(key); return key;
            }));
        Assert.All(Assert.Single(allocated), value => Assert.Equal(0, value));
        var expanded = Options(); expanded.Url = "https://origin.example/" + new string('日', 2000);
        Assert.Throws<InvalidOperationException>(() => Catalog(expanded));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("a-b_c1", true)]
    public void SourceNamesRejectNullAndEmptyWhileAllowingTheDeclaredAsciiTokenAlphabet(string? name, bool valid)
    {
        var options = Options(); options.Name = name!;
        if (!valid) Assert.Throws<InvalidOperationException>(() => Catalog(options));
        else { using var sources = Catalog(options); Assert.Equal(name, sources.Find(name!, "p")!.Name); }
    }

    [Fact]
    public void AllPermittedHeaderTokenPunctuationIsAcceptedWithoutAllowingUnicodeNames()
    {
        var options = Options(); options.Headers = new() { ["!#$%&'*+-.^_`|~"] = "" };
        using (var sources = Catalog(options)) Assert.Single(sources.Find("source", "p")!.Headers);
        foreach (string name in new[] { "名", "K", "X-K" })
        {
            options.Headers = new() { [name] = "value" };
            Assert.Throws<InvalidOperationException>(() => Catalog(options));
        }
        options.Url = null!; Assert.Throws<InvalidOperationException>(() => Catalog(options));
    }

    [Fact]
    public void ExactAggregateHeaderAndPrintableValueBoundsRemainValid()
    {
        var options = Options();
        options.Headers = new() { ["A"] = new string('~', 4096), ["B"] = new string('x', 4096),
            ["C"] = new string('x', 4096), ["D"] = new string('x', 4092) };
        using (var sources = Catalog(options)) Assert.Equal(4, sources.Find("source", "p")!.Headers.Count);
        options.Headers["D"] += "x";
        Assert.Throws<InvalidOperationException>(() => Catalog(options));
    }

    [Fact]
    public void MaximumEscapedEntityTagsAndObjectSizesKeepTheirEncodingReservation()
    {
        using var sources = Catalog();
        var source = sources.Find("source", "p")!;
        string tag = "\"" + new string('&', 1022) + "\"";
        Assert.True(Snapshot(source, source.MaxObjectBytes, tag).IsValid(source));
        Assert.False(HttpObjectSnapshot.IsETag("\"" + new string('&', 1023) + "\""));
    }

    [Fact]
    public void InvalidEncodedKeysClearEvenPartiallyDecodedOwnedStorage()
    {
        byte[] key = Enumerable.Repeat((byte)19, 32).ToArray();
        Assert.Throws<InvalidOperationException>(() => HttpSources.DecodeOwnedKey(new string('!', 44), key));
        Assert.All(key, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData("\"\"", true)]
    [InlineData("\"tag\"", true)]
    [InlineData("*", false)]
    [InlineData("W/\"tag\"", false)]
    [InlineData(" \"tag\"", false)]
    [InlineData("\"tag\" ", false)]
    [InlineData("tag", false)]
    [InlineData(null, false)]
    public void ValidatorsMustBeCanonicalStrongEntityTags(string? tag, bool valid) =>
        Assert.Equal(valid, HttpObjectSnapshot.IsETag(tag));
}

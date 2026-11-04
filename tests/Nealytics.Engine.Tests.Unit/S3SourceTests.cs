using Nealytics.Engine.Features.S3Import;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class S3SourceTests
{
    [Fact]
    public void DefaultDeclarationsContainNoConnectionOrCredentialMaterial()
    {
        var options = new S3SourceOptions();
        Assert.All(new[] { options.Name, options.ProjectId, options.Endpoint, options.Bucket, options.Region,
            options.Prefix, options.AccessKey, options.SecretKey, options.SessionToken }, value => Assert.Empty(value));
        Assert.True(options.PathStyle);
        Assert.False(options.AllowInsecureHttp);
        Assert.Equal(1_099_511_627_776, options.MaxObjectBytes);
        Assert.Equal(30, options.RequestTimeoutSeconds);
        options = S3TestSupport.Options(); options.Bucket = "0az9";
        Assert.NotNull(S3TestSupport.Source(options));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("count")]
    [InlineData("ndjson")]
    [InlineData("jobs")]
    public void StartupRejectsMissingPrerequisitesAndUnboundedDeclarations(string scenario)
    {
        Assert.Equal("S3 accepts at most 32 sources and requires NDJSON import jobs.",
            Assert.Throws<InvalidOperationException>(() => S3TestSupport.Catalog(configure: options =>
            {
                switch (scenario)
                {
                    case "null": options.S3Sources = null!; break;
                    case "count": options.S3Sources = Enumerable.Range(0, 33).Select(_ => S3TestSupport.Options()).ToList(); break;
                    case "ndjson": options.EnableNdjsonImport = false; break;
                    case "jobs": options.EnableImportJobs = false; break;
                }
            })).Message);
    }

    [Theory]
    [InlineData("declaration")]
    [InlineData("name-null")]
    [InlineData("name-empty")]
    [InlineData("name-long")]
    [InlineData("name-character")]
    [InlineData("project-null")]
    [InlineData("project-space")]
    [InlineData("project-long")]
    [InlineData("project-unknown")]
    [InlineData("duplicate")]
    [InlineData("endpoint-invalid")]
    [InlineData("endpoint-http")]
    [InlineData("endpoint-scheme")]
    [InlineData("endpoint-path")]
    [InlineData("endpoint-user")]
    [InlineData("endpoint-query")]
    [InlineData("endpoint-fragment")]
    [InlineData("bucket-null")]
    [InlineData("bucket-short")]
    [InlineData("bucket-long")]
    [InlineData("bucket-start")]
    [InlineData("bucket-end")]
    [InlineData("bucket-case")]
    [InlineData("bucket-dots")]
    [InlineData("region-null")]
    [InlineData("region-long")]
    [InlineData("prefix-null")]
    [InlineData("prefix-long")]
    [InlineData("prefix-unicode")]
    [InlineData("access-empty")]
    [InlineData("access-long")]
    [InlineData("secret-null")]
    [InlineData("secret-empty")]
    [InlineData("secret-long")]
    [InlineData("secret-space")]
    [InlineData("token-null")]
    [InlineData("token-long")]
    [InlineData("token-control")]
    [InlineData("virtual-ip")]
    [InlineData("size")]
    [InlineData("timeout-zero")]
    [InlineData("timeout-long")]
    public void InvalidSettingsFailWithoutExposingConfigurationValues(string scenario)
    {
        var source = S3TestSupport.Options();
        Assert.Equal("S3 source settings are invalid or conflict with a declared source.",
            Assert.Throws<InvalidOperationException>(() => S3TestSupport.Catalog(source, options =>
            {
                switch (scenario)
                {
                    case "declaration": options.S3Sources = [null!]; break;
                    case "name-null": source.Name = null!; break;
                    case "name-empty": source.Name = ""; break;
                    case "name-long": source.Name = new string('a', 65); break;
                    case "name-character": source.Name = "source/other"; break;
                    case "project-null": source.ProjectId = null!; break;
                    case "project-space": source.ProjectId = " "; break;
                    case "project-long": source.ProjectId = new string('a', 257); break;
                    case "project-unknown": options.ProjectSchemas = [new() { ProjectId = "other" }]; break;
                    case "duplicate": options.S3Sources.Add(S3TestSupport.Options()); break;
                    case "endpoint-invalid": source.Endpoint = "invalid"; break;
                    case "endpoint-http": source.Endpoint = "http://store.example"; break;
                    case "endpoint-scheme": source.Endpoint = "ftp://store.example"; source.AllowInsecureHttp = true; break;
                    case "endpoint-path": source.Endpoint += "/path"; break;
                    case "endpoint-user": source.Endpoint = "https://private:password@store.example"; break;
                    case "endpoint-query": source.Endpoint += "?credential=private"; break;
                    case "endpoint-fragment": source.Endpoint += "#private"; break;
                    case "bucket-null": source.Bucket = null!; break;
                    case "bucket-short": source.Bucket = "ab"; break;
                    case "bucket-long": source.Bucket = new string('a', 64); break;
                    case "bucket-start": source.Bucket = "-abc"; break;
                    case "bucket-end": source.Bucket = "abc-"; break;
                    case "bucket-case": source.Bucket = "Abc"; break;
                    case "bucket-dots": source.Bucket = "a..b"; break;
                    case "region-null": source.Region = null!; break;
                    case "region-long": source.Region = new string('a', 65); break;
                    case "prefix-null": source.Prefix = null!; break;
                    case "prefix-long": source.Prefix = new string('é', 513); break;
                    case "prefix-unicode": source.Prefix = "\ud800"; break;
                    case "access-empty": source.AccessKey = ""; break;
                    case "access-long": source.AccessKey = new string('a', 129); break;
                    case "secret-null": source.SecretKey = null!; break;
                    case "secret-empty": source.SecretKey = ""; break;
                    case "secret-long": source.SecretKey = new string('a', 257); break;
                    case "secret-space": source.SecretKey = "private key"; break;
                    case "token-null": source.SessionToken = null!; break;
                    case "token-long": source.SessionToken = new string('a', 4097); break;
                    case "token-control": source.SessionToken = "private\r\n"; break;
                    case "virtual-ip": source.Endpoint = "https://127.0.0.1"; source.PathStyle = false; break;
                    case "size": source.MaxObjectBytes = 0; break;
                    case "timeout-zero": source.RequestTimeoutSeconds = 0; break;
                    case "timeout-long": source.RequestTimeoutSeconds = 121; break;
                }
            })).Message);
    }

    [Fact]
    public void SourcesAreOptionalFrozenOrdinalAndProjectScoped()
    {
        var source = S3TestSupport.Options();
        var catalog = S3TestSupport.Catalog(source);
        var selected = catalog.Find("source", "p")!;
        source.Name = "changed"; source.SecretKey = "changed"; source.ProjectId = "changed";
        Assert.Equal(1, catalog.Count);
        Assert.Same(selected, catalog.Find("source", "p"));
        Assert.Equal("p", selected.ProjectId);
        Assert.NotEqual("changed", selected.SecretKey);
        Assert.Null(catalog.Find("Source", "p"));
        Assert.Null(catalog.Find("source", "P"));
        Assert.Null(catalog.Find("missing", "p"));
        var options = new TelemetryEngineOptions();
        var projects = new ProjectCatalog(options, ProjectContext.Compile("baseline", options));
        Assert.Equal(0, new S3Sources(options, projects).Count);
        Assert.Empty(options.S3Sources);
    }

    [Fact]
    public void MaximumSupportedDeclarationsAndFieldBoundariesRemainUsable()
    {
        var source = S3TestSupport.Options();
        source.Name = new string('n', 64); source.ProjectId = new string('p', 256);
        source.Bucket = "a" + new string('-', 61) + "1"; source.Region = new string('r', 64);
        source.AccessKey = new string('a', 128); source.SecretKey = new string('a', 256);
        source.SessionToken = new string('t', 4096); source.Prefix = new string('é', 512);
        source.RequestTimeoutSeconds = 120; source.MaxObjectBytes = long.MaxValue;
        Assert.NotNull(S3TestSupport.Catalog(source).Find(source.Name, source.ProjectId));
        Assert.Equal(32, S3TestSupport.Catalog(configure: options => options.S3Sources = Enumerable.Range(0, 32)
            .Select(index => new S3SourceOptions
            {
                Name = "source-" + index, ProjectId = "p", Endpoint = "http://127.0.0.1:9000", AllowInsecureHttp = true,
                Bucket = "abc", Region = "test_1", AccessKey = "key_1", SecretKey = "!~", RequestTimeoutSeconds = 1,
                MaxObjectBytes = 1
            }).ToList()).Count);
    }

    [Fact]
    public void KeysAndVersionsUseStrictUtf8AndLiteralPrefixScope()
    {
        var source = S3TestSupport.Source() with { Prefix = "prefix/" };
        Assert.True(S3Sources.Key(source, "prefix/../literal"));
        Assert.False(S3Sources.Key(source, "Prefix/file"));
        Assert.False(S3Sources.Key(source, "prefix-suffix/file"));
        Assert.False(S3Sources.Key(source, null));
        Assert.False(S3Sources.Key(source, ""));
        Assert.False(S3Sources.Key(source, "prefix/\ud800"));
        Assert.True(S3Sources.Key(source with { Prefix = "" }, new string('é', 512)));
        Assert.False(S3Sources.Key(source with { Prefix = "" }, new string('é', 513)));
        Assert.True(S3Sources.Version(null));
        Assert.True(S3Sources.Version("null"));
        Assert.True(S3Sources.Version(new string('é', 1024)));
        Assert.False(S3Sources.Version(new string('é', 1025)));
        Assert.False(S3Sources.Version(""));
        Assert.False(S3Sources.Version("\ud800"));
    }
}

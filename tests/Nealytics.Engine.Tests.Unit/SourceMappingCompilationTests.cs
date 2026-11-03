using Nealytics.Engine.Features.SourceMapping;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class SourceMappingCompilationTests
{
    [Theory]
    [InlineData("eventId")]
    [InlineData("timestamp")]
    [InlineData("projectId")]
    [InlineData("tenantId")]
    [InlineData("eventType")]
    public void EveryRequiredTargetMustBeDeclared(string target)
    {
        var options = SourceRecordMappingTests.Options();
        options.Fields.Remove(target);
        options.Fields["userId"] = new() { Default = "u" };
        Assert.Throws<InvalidOperationException>(() => SourceRecordMapping.Compile(options));
    }

    [Theory]
    [InlineData("eventId", false)]
    [InlineData("eventId", true)]
    [InlineData("timestamp", false)]
    [InlineData("timestamp", true)]
    public void IdentityAndTimeRequirePathsAndForbidFallbacks(string target, bool withPath)
    {
        var options = SourceRecordMappingTests.Options();
        options.Fields[target] = new() { Path = withPath ? ["value"] : [], Default = "explicit" };
        Assert.Throws<InvalidOperationException>(() => SourceRecordMapping.Compile(options));
    }

    [Theory]
    [InlineData("EventId")]
    [InlineData("_walRecordId")]
    [InlineData("seq")]
    [InlineData("dimensions.")]
    [InlineData("measures.")]
    [InlineData("anything")]
    public void UnsupportedTargetsCannotChangeTheTelemetryContract(string target)
    {
        var options = SourceRecordMappingTests.Options();
        options.Fields[target] = new() { Default = "x" };
        Assert.Throws<InvalidOperationException>(() => SourceRecordMapping.Compile(options));
    }

    [Fact]
    public void NullAndEmptyDeclarationsFailBeforeIngestionStarts()
    {
        Assert.Throws<InvalidOperationException>(() => SourceRecordMapping.Compile(new() { Fields = null! }));
        Assert.Throws<InvalidOperationException>(() => SourceRecordMapping.Compile(new()));
        foreach (SourceFieldOptions? field in new SourceFieldOptions?[] { null, new() { Path = null! }, new() })
        {
            var options = SourceRecordMappingTests.Options();
            options.Fields["userId"] = field!;
            Assert.Throws<InvalidOperationException>(() => SourceRecordMapping.Compile(options));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyOrNullPathSegmentsAreRejected(string? segment)
    {
        var options = SourceRecordMappingTests.Options();
        options.Fields["userId"] = new() { Path = [segment!] };
        Assert.Throws<InvalidOperationException>(() => SourceRecordMapping.Compile(options));
    }

    [Fact]
    public void AllConfigurationCapsAcceptTheirBoundaryAndRejectTheNextValue()
    {
        var options = SourceRecordMappingTests.Options();
        string target = "dimensions." + new string('x', 117);
        options.Fields[target] = new() { Path = Enumerable.Repeat(new string('s', 128), 16).ToArray(), Default = new string('d', 4096) };
        SourceRecordMapping.Compile(options);
        options.Fields[target + "x"] = options.Fields[target];
        Assert.Throws<InvalidOperationException>(() => SourceRecordMapping.Compile(options));
        options.Fields.Remove(target + "x");
        options.Fields[target].Path = Enumerable.Repeat("s", 17).ToArray();
        Assert.Throws<InvalidOperationException>(() => SourceRecordMapping.Compile(options));
        options.Fields[target].Path = [new string('s', 129)];
        Assert.Throws<InvalidOperationException>(() => SourceRecordMapping.Compile(options));
        options.Fields[target].Path = ["s"];
        options.Fields[target].Default = new string('d', 4097);
        Assert.Throws<InvalidOperationException>(() => SourceRecordMapping.Compile(options));
        options.Fields.Remove(target);
        for (int i = 0; i < 59; i++) options.Fields["dimensions.d" + i] = new() { Default = "" };
        SourceRecordMapping.Compile(options);
        options.Fields["dimensions.excess"] = new() { Default = "" };
        Assert.Throws<InvalidOperationException>(() => SourceRecordMapping.Compile(options));
    }

    [Fact]
    public void NdjsonProjectMappingsAreCaseSensitiveFrozenAndOptional()
    {
        var options = new TelemetryEngineOptions
        {
            NdjsonSourceMappings = new() { ["p"] = SourceRecordMappingTests.Options() }
        };
        var registry = new NdjsonSourceMappings(options, Catalog(options));
        options.NdjsonSourceMappings.Clear();
        Assert.NotNull(registry.Find("p"));
        Assert.Null(registry.Find("P"));
        Assert.Null(registry.Find("other"));
        Assert.Null(new NdjsonSourceMappings(new(), Catalog(new())).Find("p"));
    }

    [Fact]
    public void NdjsonRegistryRejectsNullUnknownOrOversizedProjectDeclarations()
    {
        var options = new TelemetryEngineOptions { NdjsonSourceMappings = null! };
        Assert.Throws<InvalidOperationException>(() => new NdjsonSourceMappings(options, Catalog(options)));
        options.NdjsonSourceMappings = new();
        foreach (string key in new[] { "", " ", new string('p', 257) })
        {
            options.NdjsonSourceMappings = new() { [key] = SourceRecordMappingTests.Options() };
            Assert.Throws<InvalidOperationException>(() => new NdjsonSourceMappings(options, Catalog(options)));
        }
        options.NdjsonSourceMappings = new() { [new string('p', 256)] = SourceRecordMappingTests.Options() };
        _ = new NdjsonSourceMappings(options, Catalog(options));
        options.NdjsonSourceMappings = new() { ["p"] = null! };
        Assert.Throws<InvalidOperationException>(() => new NdjsonSourceMappings(options, Catalog(options)));
        options.ProjectSchemas = [new() { ProjectId = "declared" }];
        options.NdjsonSourceMappings = new() { ["unknown"] = SourceRecordMappingTests.Options() };
        Assert.Throws<InvalidOperationException>(() => new NdjsonSourceMappings(options, Catalog(options)));
        options.ProjectSchemas = [];
        options.NdjsonSourceMappings.Clear();
        for (int i = 0; i < 128; i++) options.NdjsonSourceMappings["p" + i] = SourceRecordMappingTests.Options();
        _ = new NdjsonSourceMappings(options, Catalog(options));
        options.NdjsonSourceMappings["excess"] = SourceRecordMappingTests.Options();
        Assert.Throws<InvalidOperationException>(() => new NdjsonSourceMappings(options, Catalog(options)));
    }

    private static ProjectCatalog Catalog(TelemetryEngineOptions options) => new(options, ProjectContext.Compile("baseline", options));
}

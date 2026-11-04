using System.Text;
using Nealytics.Engine.Features.ImportCsv;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class CsvMappingTests
{
    internal static SourceMappingOptions Options()
    {
        var options = SourceRecordMappingTests.Options();
        foreach (var field in options.Fields.Values)
            if (field.Path.Length != 0) field.Path = [field.Path[^1]];
        return options;
    }

    [Fact]
    public void HeaderBindingsAreOrdinalReorderableFrozenAndIndependentOfCallerMutation()
    {
        var options = Options();
        options.Fields["dimensions.tag"] = new() { Path = ["tag"] };
        options.Fields["measures.amount"] = new() { Path = ["amount"] };
        options.Fields["userId"] = new() { Default = "constant" };
        var mapping = new CsvProjectMapping(options);
        options.Fields["eventId"].Path[0] = "changed";
        options.Fields["userId"].Default = "changed";
        string[] header = ["amount", "time", "tenant", "id", "tag", "_walRecordId"];
        var columns = Assert.IsAssignableFrom<IReadOnlyDictionary<string, int>>(mapping.BindHeader(header));
        header[3] = "changed";
        var payload = mapping.Read(columns,
            ["7.50", "2026-01-01T00:00:00Z", "t", KafkaTestSupport.Identity, "日本語, é\nquoted", Guid.NewGuid().ToString()], 1024);
        Assert.Equal(Guid.Parse(KafkaTestSupport.Identity), payload.EventId);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), payload.Timestamp);
        Assert.Equal("p", payload.ProjectId);
        Assert.Equal("t", payload.TenantId);
        Assert.Equal("action", payload.EventType);
        Assert.Equal("constant", payload.UserId);
        Assert.Equal("日本語, é\nquoted", Assert.Single(payload.Dimensions!).Value);
        Assert.Equal("7.50", Assert.Single(payload.Measures!).Value);
        Assert.Equal(Guid.Empty, payload.WalRecordId);
        Assert.Equal(0u, payload.Seq);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("id", false)]
    [InlineData("ID", true)]
    [InlineData("extra", true)]
    public void EmptyDuplicateAndMissingColumnsFailBeforeDataAdmission(string last, bool accepted)
    {
        var mapping = new CsvProjectMapping(Options());
        Assert.Equal(accepted, mapping.BindHeader(["id", "time", "tenant", last]) is not null);
    }

    [Fact]
    public void HeaderLookupNeverFallsBackToCaseInsensitiveColumnNames() =>
        Assert.Null(new CsvProjectMapping(Options()).BindHeader(["ID", "time", "tenant"]));

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void HeaderNamesUseAnExplicitCharacterBound(int count, bool accepted)
    {
        Assert.Equal(accepted, new CsvProjectMapping(Options()).BindHeader(["id", "time", "tenant", new string('é', count)]) is not null);
    }

    [Theory]
    [InlineData("eventId")]
    [InlineData("timestamp")]
    public void IdentityAndTimeCannotBeSynthesizedOrDefaulted(string target)
    {
        var options = Options();
        options.Fields[target] = new() { Default = target == "eventId" ? KafkaTestSupport.Identity : "2026-01-01T00:00:00Z" };
        Assert.Throws<InvalidOperationException>(() => new CsvProjectMapping(options));
    }

    [Fact]
    public void NestedPathsCannotBeMisinterpretedAsCsvColumnNames()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new CsvProjectMapping(SourceRecordMappingTests.Options()));
        Assert.Equal("CSV bindings require literal single-column paths.", error.Message);
    }

    [Fact]
    public void MappingTextBudgetIncludesRepeatedBindingsAndDefaultsAndKeepsEmptyCells()
    {
        var options = Options();
        options.Fields["userId"] = new() { Path = ["tenant"] };
        var mapping = new CsvProjectMapping(options);
        var columns = mapping.BindHeader(["id", "time", "tenant"])!;
        string[] row = [KafkaTestSupport.Identity, "2026-01-01T00:00:00Z", ""];
        var payload = mapping.Read(columns, row, 63);
        Assert.Equal("", payload.TenantId);
        Assert.Equal("", payload.UserId);
        Assert.Throws<InvalidDataException>(() => mapping.Read(columns, row, 62));
    }

    [Fact]
    public void MissingDeclaredColumnsAreRejectedEvenWhenThatBindingHasAFallback()
    {
        var options = Options();
        options.Fields["userId"] = new() { Path = ["user"], Default = "fallback" };
        Assert.Null(new CsvProjectMapping(options).BindHeader(["id", "time", "tenant"]));
    }

    [Fact]
    public void CsvConfigurationStartsDisabledAndIsBoundedScopedAndFrozen()
    {
        var defaults = new TelemetryEngineOptions();
        Assert.False(defaults.EnableCsvImport);
        Assert.Empty(defaults.CsvSourceMappings);
        defaults.CsvSourceMappings["p"] = Options();
        var mappings = new CsvSourceMappings(defaults, Catalog(defaults));
        defaults.CsvSourceMappings.Clear();
        Assert.NotNull(mappings.Find("p"));
        Assert.Null(mappings.Find("P"));
        Assert.Null(mappings.Find("other"));
        defaults.CsvSourceMappings = null!;
        Assert.Equal("CSV accepts at most 128 project source mappings.",
            Assert.Throws<InvalidOperationException>(() => new CsvSourceMappings(defaults, Catalog(defaults))).Message);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void MappingCountHasAnIndependentCeiling(int count, bool accepted)
    {
        var options = new TelemetryEngineOptions { CsvSourceMappings = Enumerable.Range(0, count).ToDictionary(i => "p" + i, _ => Options()) };
        if (accepted) Assert.NotNull(new CsvSourceMappings(options, Catalog(options)));
        else Assert.Throws<InvalidOperationException>(() => new CsvSourceMappings(options, Catalog(options)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    public void DeclarationsRequireAKnownNonblankProjectAndSafeDiagnostics(string project)
    {
        var options = new TelemetryEngineOptions
        {
            ProjectSchemas = [new() { ProjectId = "known" }],
            CsvSourceMappings = new() { [project] = Options() }
        };
        var error = Assert.Throws<InvalidOperationException>(() => new CsvSourceMappings(options, Catalog(options)));
        Assert.Equal("CSV source mappings require configured project contexts and declarations.", error.Message);
    }
    [Theory]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("p", true)]
    public void LegacyFallbackCannotMakeInvalidProjectNamesValidDeclarations(string project, bool accepted)
    {
        var options = new TelemetryEngineOptions { CsvSourceMappings = new() { [project] = Options() } };
        if (accepted) Assert.NotNull(new CsvSourceMappings(options, Catalog(options)));
        else Assert.Throws<InvalidOperationException>(() => new CsvSourceMappings(options, Catalog(options)));
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void ProjectNameLimitHasAnExactInclusiveBoundary(int length, bool accepted)
    {
        var options = new TelemetryEngineOptions { CsvSourceMappings = new() { [new string('p', length)] = Options() } };
        if (accepted) Assert.NotNull(new CsvSourceMappings(options, Catalog(options)));
        else Assert.Throws<InvalidOperationException>(() => new CsvSourceMappings(options, Catalog(options)));
    }

    [Fact]
    public void NullMappingsAreRejectedAndSingleCharacterExtraHeadersAreAccepted()
    {
        var options = new TelemetryEngineOptions { CsvSourceMappings = new() { ["p"] = null! } };
        Assert.Throws<InvalidOperationException>(() => new CsvSourceMappings(options, Catalog(options)));
        Assert.NotNull(new CsvProjectMapping(Options()).BindHeader(["id", "time", "tenant", "x"]));
    }

    private static ProjectCatalog Catalog(TelemetryEngineOptions options) =>
        new(options, ProjectContext.Compile("baseline", options));

}

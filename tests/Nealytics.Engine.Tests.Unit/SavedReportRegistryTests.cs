using Microsoft.Extensions.Configuration;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public sealed class SavedReportRegistryTests
{
    internal static TelemetryEngineOptions Options()
    {
        var options = NamedMetricRegistryTests.Options();
        options.SavedReports = [new() { Name = "overview", Fields = [new() { Name = "events", Metric = "activity" }] }];
        return options;
    }

    internal static SavedReportRegistry Build(TelemetryEngineOptions options)
    {
        var dimensions = new DimensionRegistry(options);
        var measures = new MeasureRegistry(options, dimensions);
        return new SavedReportRegistry(options, new NamedMetricRegistry(options, measures),
            new EventSegmentRegistry(options, new QueryColumns(dimensions), measures));
    }

    [Fact]
    public void DefaultsAndMissingDeclarationsKeepExistingDeploymentsValid()
    {
        var options = new TelemetryEngineOptions();
        Assert.Equal(128, options.MaxSavedReports);
        Assert.Empty(options.SavedReports);
        Assert.Empty(Build(options).Declared);
        options.SavedReports = null!;
        Assert.Empty(Build(options).Declared);
        var declaration = new SavedReportOptions();
        Assert.Equal("", declaration.Name);
        Assert.Equal(1, declaration.Version);
        Assert.Equal("", declaration.Segment);
        Assert.Empty(declaration.Fields);
        var field = new SavedReportFieldOptions();
        Assert.Equal("", field.Name);
        Assert.Equal("", field.Metric);
    }

    [Fact]
    public void MissingDependenciesAreRejectedIndividually()
    {
        var options = Options();
        var measures = new MeasureRegistry(options, new DimensionRegistry(options));
        var metrics = new NamedMetricRegistry(options, measures);
        var segments = new EventSegmentRegistry(options, new QueryColumns(new DimensionRegistry(options)), measures);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => new SavedReportRegistry(null!, metrics, segments)).ParamName);
        Assert.Equal("metrics", Assert.Throws<ArgumentNullException>(() => new SavedReportRegistry(options, null!, segments)).ParamName);
        Assert.Equal("segments", Assert.Throws<ArgumentNullException>(() => new SavedReportRegistry(options, metrics, null!)).ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    public void InvalidCapacityIsRejected(int capacity)
    {
        var options = Options();
        options.MaxSavedReports = capacity;
        Assert.Equal("TelemetryEngine:MaxSavedReports must be between 1 and 1024.",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(1024)]
    public void CapacityIncludesTheExactLimitAndRejectsOneMore(int capacity)
    {
        var options = Options();
        options.MaxSavedReports = capacity;
        options.SavedReports = Enumerable.Range(0, capacity).Select(index => new SavedReportOptions
        {
            Name = "report_" + index, Fields = [new() { Name = "value", Metric = "activity" }]
        }).ToList();
        Assert.Equal(capacity, Build(options).Declared.Count);
        options.SavedReports.Add(new() { Name = "extra", Fields = [new() { Name = "value", Metric = "activity" }] });
        Assert.Equal("TelemetryEngine:SavedReports exceeds MaxSavedReports.",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Upper")]
    [InlineData("1first")]
    [InlineData("a-b")]
    [InlineData("a;SELECT")]
    public void NamesAndAliasesUseTheSameStrictGrammar(string? name)
    {
        var options = Options();
        options.SavedReports[0].Name = name!;
        Assert.Equal("TelemetryEngine:SavedReports[0]: name must match [a-z][a-z0-9_]{0,62}.",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
        options.SavedReports[0].Name = "overview";
        options.SavedReports[0].Fields[0].Name = name!;
        Assert.Equal("TelemetryEngine:SavedReports[0]: field name must match [a-z][a-z0-9_]{0,62}.",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Fact]
    public void NameLengthIncludesOneAndSixtyThreeCharacters()
    {
        foreach (int length in new[] { 1, 63 })
        {
            var options = Options();
            options.SavedReports[0].Name = " " + new string('a', length) + " ";
            options.SavedReports[0].Fields[0].Name = new string('b', length);
            var report = Assert.Single(Build(options).Declared);
            Assert.Equal(new string('a', length), report.Name);
            Assert.Equal(new string('b', length), Assert.Single(report.Fields).Name);
        }
        NamesAndAliasesUseTheSameStrictGrammar(new string('a', 64));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    public void OnlyTheCurrentDefinitionVersionIsAccepted(int version)
    {
        var options = Options();
        options.SavedReports[0].Version = version;
        Assert.Equal("TelemetryEngine:SavedReports[0]: only version 1 is supported.",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData(0, "declaration is null")]
    [InlineData(1, "name is duplicated")]
    [InlineData(2, "provide between 1 and 16 fields")]
    [InlineData(3, "provide between 1 and 16 fields")]
    [InlineData(4, "provide between 1 and 16 fields")]
    [InlineData(5, "fields[0] is null")]
    [InlineData(6, "field name is duplicated")]
    [InlineData(7, "fields[0] metric is not declared")]
    [InlineData(8, "fields[0] metric is not declared")]
    [InlineData(9, "segment is not declared")]
    public void InvalidDeclarationsProduceIndexedDiagnostics(int invalidCase, string reason)
    {
        var options = Options();
        var report = options.SavedReports[0];
        switch (invalidCase)
        {
            case 0: options.SavedReports[0] = null!; break;
            case 1: options.SavedReports.Add(new() { Name = " overview " }); break;
            case 2: report.Fields = null!; break;
            case 3: report.Fields.Clear(); break;
            case 4: report.Fields = Enumerable.Range(0, 17).Select(index => new SavedReportFieldOptions { Name = "f" + index, Metric = "activity" }).ToList(); break;
            case 5: report.Fields[0] = null!; break;
            case 6: report.Fields.Add(new() { Name = " events ", Metric = "activity" }); break;
            case 7: report.Fields[0].Metric = null!; break;
            case 8: report.Fields[0].Metric = "Activity"; break;
            case 9: report.Segment = "missing"; break;
        }
        int index = invalidCase == 1 ? 1 : 0;
        Assert.Equal($"TelemetryEngine:SavedReports[{index}]: {reason}.",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" selected ")]
    public void DefinitionsAreCanonicalOrderedAndDetachedFromMutableConfiguration(string? segment)
    {
        var options = Options();
        options.EventSegments = [new() { Name = "selected", Filters = ["country:x"] }];
        var configured = options.SavedReports[0];
        configured.Segment = segment!;
        configured.Fields = Enumerable.Range(0, 16).Select(index => new SavedReportFieldOptions
        {
            Name = " f" + index + " ", Metric = " activity "
        }).ToList();
        var registry = Build(options);
        var definition = Assert.Single(registry.Declared);
        Assert.Same(definition, registry.Find("overview"));
        Assert.Null(registry.Find(null));
        Assert.Null(registry.Find("Overview"));
        Assert.Equal(1, definition.Version);
        Assert.Equal(string.IsNullOrWhiteSpace(segment) ? null : "selected", definition.Segment);
        Assert.Equal(Enumerable.Range(0, 16).Select(index => "f" + index), definition.Fields.Select(field => field.Name));
        Assert.All(definition.Fields, field =>
        {
            Assert.Equal("activity", field.Metric);
            Assert.Equal("activity", field.Definition.Name);
        });
        configured.Fields[0].Name = "changed";
        configured.Fields.Clear();
        options.NamedMetrics[0].Name = "changed";
        options.SavedReports.Clear();
        Assert.Equal(16, definition.Fields.Count);
        Assert.Equal("f0", definition.Fields[0].Name);
        Assert.Throws<NotSupportedException>(() => ((IList<SavedReport>)registry.Declared).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<SavedReportField>)definition.Fields).Clear());
    }

    [Fact]
    public void DefinitionsBindThroughTheExistingConfigurationProvider()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
            {"TelemetryEngine":{"NamedMetrics":[{"Name":"activity"}],"SavedReports":[{"Name":"overview","Version":1,"Fields":[{"Name":"events","Metric":"activity"}]}],"MaxSavedReports":2}}
            """));
        var options = new ConfigurationBuilder().AddJsonStream(stream).Build()
            .GetSection("TelemetryEngine").Get<TelemetryEngineOptions>()!;
        Assert.Equal(2, options.MaxSavedReports);
        Assert.Equal("activity", Assert.Single(Assert.Single(Build(options).Declared).Fields).Metric);
    }
}

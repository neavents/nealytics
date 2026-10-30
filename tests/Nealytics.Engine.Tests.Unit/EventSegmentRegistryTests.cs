using System.Text;
using Microsoft.Extensions.Configuration;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public sealed class EventSegmentRegistryTests
{
    internal static TelemetryEngineOptions Options() => new()
    {
        Dimensions = [new() { Name = "unit_key" }],
        Measures = [new() { Name = "amount", Type = "Int64", UnitDimension = "unit_key" }],
        TenantAttributes = [new() { Name = "cohort" }],
        NamedMetrics = [new() { Name = "activity" }],
        EventSegments = [new() { Name = "selected", Filters = ["unit_key:u", "amount>=2"] }]
    };

    internal static EventSegmentRegistry Build(TelemetryEngineOptions options)
    {
        var dimensions = new DimensionRegistry(options);
        return new(options, new QueryColumns(dimensions, new TenantAttributeRegistry(options)),
            new MeasureRegistry(options, dimensions));
    }

    [Fact]
    public void DefaultsAndMissingDefinitionsPreserveExistingDeployments()
    {
        var options = new TelemetryEngineOptions();
        Assert.Empty(options.EventSegments);
        Assert.Equal(128, options.MaxEventSegments);
        Assert.Empty(Build(options).Declared);
        options.EventSegments = null!;
        Assert.Empty(Build(options).Declared);
        var declaration = new EventSegmentOptions();
        Assert.Equal("", declaration.Name);
        Assert.Equal(1, declaration.Version);
        Assert.Empty(declaration.Filters);
        Assert.Empty(new EventSegment("test", 1, []).CompiledFilters);
    }

    [Fact]
    public void DependenciesAreValidatedBeforeReadingDeclarations()
    {
        var options = Options();
        var dimensions = new DimensionRegistry(options);
        var columns = new QueryColumns(dimensions);
        var measures = new MeasureRegistry(options, dimensions);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => new EventSegmentRegistry(null!, columns, measures)).ParamName);
        Assert.Equal("columns", Assert.Throws<ArgumentNullException>(() => new EventSegmentRegistry(options, null!, measures)).ParamName);
        Assert.Equal("measures", Assert.Throws<ArgumentNullException>(() => new EventSegmentRegistry(options, columns, null!)).ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1025)]
    public void InvalidCapacityFailsBeforeActivation(int maximum)
    {
        var options = Options();
        options.MaxEventSegments = maximum;
        Assert.Equal("TelemetryEngine:MaxEventSegments must be between 1 and 1024.",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(1024)]
    public void ExactCapacityPassesAndOneMoreDefinitionFails(int maximum)
    {
        var options = Options();
        options.MaxEventSegments = maximum;
        options.EventSegments = Enumerable.Range(0, maximum).Select(index => new EventSegmentOptions
        {
            Name = "s" + index, Filters = ["country:x"]
        }).ToList();
        Assert.Equal(maximum, Build(options).Declared.Count);
        options.EventSegments.Add(new() { Name = "extra", Filters = ["country:x"] });
        Assert.Equal("TelemetryEngine:EventSegments exceeds MaxEventSegments.",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    private static readonly (Action<TelemetryEngineOptions> Change, int Index, string Reason)[] InvalidCases =
    [
        (o => o.EventSegments[0] = null!, 0, "declaration is null"),
        (o => o.EventSegments[0].Name = null!, 0, "name must match [a-z][a-z0-9_]{0,62}"),
        (o => o.EventSegments[0].Name = " ", 0, "name must match [a-z][a-z0-9_]{0,62}"),
        (o => o.EventSegments[0].Name = "A", 0, "name must match [a-z][a-z0-9_]{0,62}"),
        (o => o.EventSegments[0].Name = "1a", 0, "name must match [a-z][a-z0-9_]{0,62}"),
        (o => o.EventSegments[0].Name = "a-b", 0, "name must match [a-z][a-z0-9_]{0,62}"),
        (o => o.EventSegments[0].Name = new string('a', 64), 0, "name must match [a-z][a-z0-9_]{0,62}"),
        (o => o.EventSegments.Add(new() { Name = " selected ", Filters = ["country:x"] }), 1, "name is duplicated"),
        (o => o.EventSegments[0].Version = 0, 0, "only version 1 is supported"),
        (o => o.EventSegments[0].Version = -1, 0, "only version 1 is supported"),
        (o => o.EventSegments[0].Version = 2, 0, "only version 1 is supported"),
        (o => o.EventSegments[0].Filters = null!, 0, "provide between 1 and 32 nonblank filters"),
        (o => o.EventSegments[0].Filters = [], 0, "provide between 1 and 32 nonblank filters"),
        (o => o.EventSegments[0].Filters = Enumerable.Repeat("country:x", 33).ToList(), 0, "provide between 1 and 32 nonblank filters"),
        (o => o.EventSegments[0].Filters = [null!], 0, "provide between 1 and 32 nonblank filters"),
        (o => o.EventSegments[0].Filters = ["country:x", " "], 0, "provide between 1 and 32 nonblank filters"),
        (o => o.EventSegments[0].Filters = ["tenant.cohort:g"], 0, "tenant attributes are not event fields")
    ];

    public static TheoryData<int, int, string> InvalidDeclarations
    {
        get
        {
            var cases = new TheoryData<int, int, string>();
            for (int index = 0; index < InvalidCases.Length; index++)
            {
                cases.Add(index, InvalidCases[index].Index, InvalidCases[index].Reason);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidDeclarations))]
    public void InvalidDefinitionsRetainTheirConfigurationPath(int scenario, int index, string reason)
    {
        var options = Options();
        InvalidCases[scenario].Change(options);
        Assert.Equal($"TelemetryEngine:EventSegments[{index}]: {reason}.",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData("unknown:x")]
    [InlineData("unknown>1")]
    [InlineData("amount=NaN")]
    [InlineData("amount=Infinity")]
    [InlineData("amount=1e309")]
    [InlineData("amount=bad")]
    [InlineData("amount=")]
    [InlineData("country=x")]
    [InlineData("bad syntax")]
    public void InvalidTypedConditionsFailWithTheExistingFilterDiagnostic(string filter)
    {
        var options = Options();
        options.EventSegments[0].Filters = [filter];
        var dimensions = new DimensionRegistry(options);
        var columns = new QueryColumns(dimensions, new TenantAttributeRegistry(options));
        var measures = new MeasureRegistry(options, dimensions);
        var parsed = FilterParser.Parse([filter], columns, measures, 256);
        Assert.NotEqual(FilterParser.Outcome.Ok, parsed.Outcome);
        Assert.Equal("TelemetryEngine:EventSegments[0]: " + FilterRejection.Message(parsed, columns, measures, 256) + ".",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetiredFieldsCannotEnterTheCompiledSelection(bool measure)
    {
        var options = Options();
        options.EventSegments[0].Filters = [measure ? "amount>1" : "unit_key:u"];
        if (measure)
        {
            options.Measures[0].Retired = true;
        }
        else
        {
            options.Dimensions[0].Retired = true;
            options.Measures[0].UnitDimension = "";
        }
        Assert.Contains("not a known", Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Fact]
    public void ExactLimitsAndCopiedDefinitionsAreImmutable()
    {
        var options = Options();
        options.EventSegments[0].Name = " " + new string('a', 63) + " ";
        options.EventSegments[0].Filters = Enumerable.Repeat("country:" + new string('x', 256), 32).ToList();
        var registry = Build(options);
        var segment = Assert.Single(registry.Declared);
        Assert.Equal(1, segment.Version);
        Assert.Equal(32, segment.Filters.Count);
        Assert.Equal(32, segment.CompiledFilters.Count);
        Assert.Same(segment, registry.Find(new string('a', 63)));
        Assert.Null(registry.Find(new string('A', 63)));
        Assert.Null(registry.Find(null));
        Assert.Null(registry.Find("unknown"));
        options.EventSegments[0].Filters[0] = "country:changed";
        options.EventSegments.Clear();
        Assert.Equal(new string('x', 256), segment.CompiledFilters[0].Value);
        Assert.Equal("country:" + new string('x', 256), segment.Filters[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<EventSegment>)registry.Declared).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)segment.Filters)[0] = "country:changed");
        Assert.Throws<NotSupportedException>(() => ((IList<QueryFilter>)segment.CompiledFilters).Clear());
        options.EventSegments.Add(new() { Name = "a", Filters = ["country:" + new string('x', 257)] });
        Assert.Contains("256", Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData("=", FilterComparison.Equal)]
    [InlineData("!=", FilterComparison.NotEqual)]
    [InlineData("<", FilterComparison.Less)]
    [InlineData("<=", FilterComparison.LessOrEqual)]
    [InlineData(">", FilterComparison.Greater)]
    [InlineData(">=", FilterComparison.GreaterOrEqual)]
    public void NumericConditionsCompileUsingTheExistingFiniteFloat64Contract(string operation, FilterComparison comparison)
    {
        var options = Options();
        options.EventSegments[0].Filters = ["amount" + operation + "1.25e1", "country:"];
        var segment = Assert.Single(Build(options).Declared);
        Assert.Equal("amount" + operation + "1.25e1", segment.Filters[0]);
        var predicate = segment.CompiledFilters[0];
        Assert.Equal("amount", predicate.Column);
        Assert.Equal("12.5", predicate.Value);
        Assert.True(predicate.IsMeasure);
        Assert.Equal(comparison, predicate.Comparison);
        Assert.Equal("", segment.CompiledFilters[1].Value);
        Assert.False(segment.CompiledFilters[1].IsMeasure);
    }

    [Fact]
    public void SchemaConfigurationBindsWithoutASeparateProvider()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"TelemetryEngine":{"MaxEventSegments":2,"EventSegments":[{"Name":"chosen","Version":1,"Filters":["country:x"]}]}}
            """));
        var options = new ConfigurationBuilder().AddJsonStream(stream).Build()
            .GetSection("TelemetryEngine").Get<TelemetryEngineOptions>()!;
        var segment = Assert.Single(Build(options).Declared);
        Assert.Equal(2, options.MaxEventSegments);
        Assert.Equal("chosen", segment.Name);
        Assert.Equal("country:x", Assert.Single(segment.Filters));
    }
}

using Microsoft.Extensions.Configuration;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public sealed class NamedMetricRegistryTests
{
    internal static TelemetryEngineOptions Options() => new()
    {
        Dimensions = [new() { Name = "unit_key" }],
        Measures = [new() { Name = "amount", Type = "Int64", UnitDimension = "unit_key" }],
        NamedMetrics = [new() { Name = "activity" }]
    };

    internal static NamedMetricRegistry Build(TelemetryEngineOptions options) =>
        new(options, new MeasureRegistry(options, new DimensionRegistry(options)));

    [Fact]
    public void ExistingConfigurationRequiresNoDefinitionsAndDefaultsAreBounded()
    {
        var options = new TelemetryEngineOptions();
        Assert.Equal(128, options.MaxNamedMetrics);
        Assert.Empty(options.NamedMetrics);
        var registry = Build(options);
        Assert.Empty(registry.Declared);
        Assert.Null(registry.Find("unknown"));
        Assert.Null(registry.Find(null));
        options.NamedMetrics = null!;
        Assert.Empty(Build(options).Declared);
        var declaration = new NamedMetricOptions();
        Assert.Equal("", declaration.Name);
        Assert.Equal(1, declaration.Version);
        Assert.Equal("count", declaration.Aggregation);
        Assert.Equal("", declaration.Measure);
        Assert.Equal("", declaration.EventType);
        Assert.Equal("", declaration.Unit);
    }

    [Fact]
    public void NullDependenciesAreRejectedAtTheBoundary()
    {
        var options = Options();
        var measures = new MeasureRegistry(options, new DimensionRegistry(options));
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => new NamedMetricRegistry(null!, measures)).ParamName);
        Assert.Equal("measures", Assert.Throws<ArgumentNullException>(() => new NamedMetricRegistry(options, null!)).ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    public void InvalidCapacityIsRejected(int limit)
    {
        var options = Options();
        options.MaxNamedMetrics = limit;
        Assert.Equal("TelemetryEngine:MaxNamedMetrics must be between 1 and 1024.",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(1024)]
    public void ExactCapacityIsAcceptedAndAnAdditionalDefinitionIsRejected(int limit)
    {
        var options = Options();
        options.MaxNamedMetrics = limit;
        options.NamedMetrics = Enumerable.Range(0, limit).Select(i => new NamedMetricOptions { Name = "m" + i }).ToList();
        Assert.Equal(limit, Build(options).Declared.Count);
        options.NamedMetrics.Add(new() { Name = "extra" });
        Assert.Equal("TelemetryEngine:NamedMetrics exceeds MaxNamedMetrics.",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    public static TheoryData<Action<TelemetryEngineOptions>, int, string> InvalidDeclarations => new()
    {
        { o => o.NamedMetrics[0] = null!, 0, "declaration is null" },
        { o => o.NamedMetrics[0].Name = null!, 0, "name must match [a-z][a-z0-9_]{0,62}" },
        { o => o.NamedMetrics[0].Name = " ", 0, "name must match [a-z][a-z0-9_]{0,62}" },
        { o => o.NamedMetrics[0].Name = "A", 0, "name must match [a-z][a-z0-9_]{0,62}" },
        { o => o.NamedMetrics[0].Name = "1a", 0, "name must match [a-z][a-z0-9_]{0,62}" },
        { o => o.NamedMetrics[0].Name = "a-b", 0, "name must match [a-z][a-z0-9_]{0,62}" },
        { o => o.NamedMetrics[0].Name = "a; SELECT 1", 0, "name must match [a-z][a-z0-9_]{0,62}" },
        { o => o.NamedMetrics[0].Name = new string('a', 64), 0, "name must match [a-z][a-z0-9_]{0,62}" },
        { o => o.NamedMetrics.Add(new() { Name = " activity " }), 1, "name is duplicated" },
        { o => o.NamedMetrics[0].Version = 0, 0, "only version 1 is supported" },
        { o => o.NamedMetrics[0].Version = -1, 0, "only version 1 is supported" },
        { o => o.NamedMetrics[0].Version = 2, 0, "only version 1 is supported" },
        { o => o.NamedMetrics[0].Aggregation = null!, 0, "aggregation must be count, sum, avg, min or max" },
        { o => o.NamedMetrics[0].Aggregation = " ", 0, "aggregation must be count, sum, avg, min or max" },
        { o => o.NamedMetrics[0].Aggregation = "p95", 0, "aggregation must be count, sum, avg, min or max" },
        { o => o.NamedMetrics[0].Aggregation = "sum", 0, "aggregation requires a measure" },
        { o => o.NamedMetrics[0].Measure = "unknown", 0, "measure is not active" },
        { o => { o.NamedMetrics[0].Measure = "amount"; o.Measures[0].Retired = true; }, 0, "measure is not active" },
        { o => { o.NamedMetrics[0].Measure = "amount"; o.Measures[0].Aggregations = "sum"; }, 0, "aggregation is not permitted by the measure" },
        { o => o.NamedMetrics[0].EventType = new string('e', 257), 0, "event type and unit must not exceed 256 characters" },
        { o => o.NamedMetrics[0].Unit = new string('u', 257), 0, "event type and unit must not exceed 256 characters" }
    };

    [Theory]
    [MemberData(nameof(InvalidDeclarations))]
    public void InvalidDefinitionsFailBeforeActivation(Action<TelemetryEngineOptions> change, int index, string reason)
    {
        var options = Options();
        change(options);
        Assert.Equal($"TelemetryEngine:NamedMetrics[{index}]: {reason}.",
            Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData("sum", "Int64", true, "unit_key")]
    [InlineData("sum", "UInt64", true, "unit_key")]
    [InlineData("sum", "Float32", false, "unit_key")]
    [InlineData("sum", "Float64", false, "unit_key")]
    [InlineData("sum", "Decimal", false, "unit_key")]
    [InlineData("avg", "Int64", false, "unit_key")]
    [InlineData("min", "Int64", false, "unit_key")]
    [InlineData("max", "Int64", false, "unit_key")]
    [InlineData("count", "Int64", false, null)]
    public void CompiledDefinitionsUseCanonicalAllowedAggregatesAndUnitSemantics(
        string aggregation, string type, bool wide, string? unitDimension)
    {
        var options = Options();
        options.Measures[0].Type = type;
        options.NamedMetrics[0] = new()
        {
            Name = " activity ", Measure = " amount ", Aggregation = " " + aggregation.ToUpperInvariant() + " ",
            EventType = " action ", Unit = " units "
        };
        var definition = Assert.Single(Build(options).Declared);
        Assert.Equal("activity", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.Equal(aggregation, definition.Aggregation);
        Assert.Equal("amount", definition.Measure);
        Assert.Equal("action", definition.EventType);
        Assert.Equal("units", definition.Unit);
        Assert.Equal(unitDimension, definition.UnitDimension);
        Assert.Equal(wide, definition.WidenSum);
    }

    [Fact]
    public void DeclarationsAreImmutableAndOptionalMetadataIsNormalized()
    {
        var options = Options();
        options.NamedMetrics[0] = new()
        {
            Name = new string('a', 63), EventType = new string('e', 256), Unit = new string('u', 256)
        };
        var registry = Build(options);
        var definition = Assert.Single(registry.Declared);
        Assert.Same(definition, registry.Find(new string('a', 63)));
        Assert.Null(registry.Find(new string('A', 63)));
        Assert.Null(definition.Measure);
        Assert.Null(definition.UnitDimension);
        Assert.False(definition.WidenSum);
        Assert.Equal(new string('e', 256), definition.EventType);
        Assert.Equal(new string('u', 256), definition.Unit);
        options.NamedMetrics[0].Name = "changed";
        options.NamedMetrics.Clear();
        Assert.Same(definition, Assert.Single(registry.Declared));
        Assert.Throws<NotSupportedException>(() => ((IList<NamedMetric>)registry.Declared)[0] = definition with { Name = "changed" });
        options.NamedMetrics.Add(new() { Name = "empty", EventType = " ", Measure = null!, Unit = null! });
        var empty = Assert.Single(Build(options).Declared);
        Assert.Null(empty.EventType);
        Assert.Null(empty.Unit);
    }

    [Fact]
    public void ExistingSchemaProviderBindsDefinitionsWithoutAnotherConfigurationSystem()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
            {"TelemetryEngine":{"NamedMetrics":[{"Name":"activity","Version":1,"Aggregation":"count","EventType":"action","Unit":"records"}],"MaxNamedMetrics":2}}
            """));
        var options = new ConfigurationBuilder().AddJsonStream(stream).Build()
            .GetSection("TelemetryEngine").Get<TelemetryEngineOptions>()!;
        var definition = Assert.Single(Build(options).Declared);
        Assert.Equal("records", definition.Unit);
        Assert.Equal("action", definition.EventType);
        Assert.Equal(2, options.MaxNamedMetrics);
    }
}

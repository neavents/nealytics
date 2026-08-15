using FluentAssertions;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public class MeasureRegistryTests
{
    private static MeasureRegistry Build(
        MeasureOptions[] measures,
        DimensionOptions[]? dimensions = null,
        int maxMeasures = 64)
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [.. dimensions ?? []],
            Measures = [.. measures],
            MaxMeasures = maxMeasures,
        };

        return new MeasureRegistry(options, new DimensionRegistry(options));
    }

    private static MeasureOptions Valid(string name = "dwell_ms", string type = "UInt32") =>
        new() { Name = name, Type = type };

    [Fact]
    public void EmptyDeclaration_IsValid()
    {
        MeasureRegistry registry = Build([]);

        registry.Active.Should().BeEmpty();
        registry.Declared.Should().BeEmpty();
    }

    [Theory]
    [InlineData("UInt8", "Nullable(UInt8)")]
    [InlineData("UInt16", "Nullable(UInt16)")]
    [InlineData("UInt32", "Nullable(UInt32)")]
    [InlineData("UInt64", "Nullable(UInt64)")]
    [InlineData("Int16", "Nullable(Int16)")]
    [InlineData("Int32", "Nullable(Int32)")]
    [InlineData("Int64", "Nullable(Int64)")]
    [InlineData("Float32", "Nullable(Float32)")]
    [InlineData("Float64", "Nullable(Float64)")]
    [InlineData("Decimal", "Nullable(Decimal(18, 4))")]
    public void DeclaredType_ResolvesToTheExactStringClickHouseReports(string declared, string expected)
    {
        MeasureRegistry registry = Build([Valid("m", declared)]);

        registry.ClickHouseType("m").Should().Be(
            expected,
            "the reconciler compares this against system.columns.type verbatim, so a value that "
            + "merely looks right turns every boot into a spurious type-mismatch refusal");
    }

    [Fact]
    public void Measures_AreNullable_SoAnAbsentValueIsNotCountedAsZero()
    {
        MeasureRegistry registry = Build([Valid("m", "Float64")]);

        registry.ClickHouseType("m").Should().StartWith(
            "Nullable(",
            "avg over a measure defaulted to 0 is wrong for every event type that does not carry it");
    }

    [Theory]
    [InlineData("Dwell_Ms")]
    [InlineData("9lives")]
    [InlineData("has;semicolon")]
    [InlineData("")]
    [InlineData("has space")]
    public void InvalidName_RefusesTheBoot(string name)
    {
        Action build = () => Build([Valid(name)]);

        build.Should().Throw<InvalidOperationException>().WithMessage("*not a valid*");
    }

    [Theory]
    [InlineData("event_id")]
    [InlineData("tenant_id")]
    [InlineData("timestamp")]
    [InlineData("object_id")]
    [InlineData("metadata_json")]
    public void NameCollidingWithACoreColumn_RefusesTheBoot(string name)
    {
        Action build = () => Build([Valid(name)]);

        build.Should().Throw<InvalidOperationException>().WithMessage("*reserved core column*");
    }

    [Fact]
    public void NameAlreadyDeclaredAsADimension_RefusesTheBoot()
    {
        Action build = () => Build(
            [Valid("menu_id")],
            [new DimensionOptions { Name = "menu_id", Type = "String" }]);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*already declared under TelemetryEngine:Dimensions*");
    }

    [Fact]
    public void NameCollidingWithARetiredDimension_StillRefusesTheBoot()
    {
        Action build = () => Build(
            [Valid("menu_id")],
            [new DimensionOptions { Name = "menu_id", Type = "String", Retired = true }]);

        build.Should().Throw<InvalidOperationException>(
            "a retired dimension keeps its column, so the name is still taken");
    }

    [Fact]
    public void DuplicateName_RefusesTheBoot()
    {
        Action build = () => Build([Valid("dwell_ms"), Valid("dwell_ms")]);

        build.Should().Throw<InvalidOperationException>().WithMessage("*a second time*");
    }

    [Fact]
    public void UnknownType_RefusesTheBootAndListsTheSupportedOnes()
    {
        Action build = () => Build([Valid("m", "BigInt")]);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown type 'BigInt'*Supported types:*");
    }

    [Fact]
    public void MissingType_RefusesTheBoot()
    {
        Action build = () => Build([new MeasureOptions { Name = "m" }]);

        build.Should().Throw<InvalidOperationException>(
            "there is no safe default width for a number — a wrong guess truncates rather than refuses");
    }

    [Fact]
    public void UnknownAggregation_RefusesTheBoot()
    {
        Action build = () => Build(
            [new MeasureOptions { Name = "m", Type = "UInt32", Aggregations = "sum,median" }]);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown aggregation 'median'*");
    }

    [Fact]
    public void NoAggregations_RefusesTheBoot()
    {
        Action build = () => Build(
            [new MeasureOptions { Name = "m", Type = "UInt32", Aggregations = "" }]);

        build.Should().Throw<InvalidOperationException>().WithMessage("*no aggregations*");
    }

    [Fact]
    public void DeclaredAggregations_AreTheOnesAllowed()
    {
        MeasureRegistry registry = Build(
            [new MeasureOptions { Name = "position", Type = "UInt16", Aggregations = "avg,min" }]);

        Measure measure = registry.Find("position")!;

        measure.Aggregations.Should().BeEquivalentTo(["avg", "min"]);
        measure.Aggregations.Contains("sum").Should().BeFalse(
            "summing a position produces a number with no referent, and the declaration is what "
            + "keeps that off the query surface");
    }

    [Fact]
    public void MinimumAboveMaximum_RefusesTheBoot()
    {
        Action build = () => Build(
            [new MeasureOptions { Name = "m", Type = "UInt32", Minimum = 10, Maximum = 1 }]);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*rejects every value it will ever receive*");
    }

    [Fact]
    public void ExceedingMaxMeasures_RefusesTheBoot()
    {
        Action build = () => Build([Valid("a"), Valid("b")], maxMeasures: 1);

        build.Should().Throw<InvalidOperationException>().WithMessage("*exceeding MaxMeasures*");
    }

    [Fact]
    public void RetiredMeasure_IsDeclaredButNotActive()
    {
        MeasureRegistry registry = Build(
            [new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Retired = true }]);

        registry.IsDeclared("dwell_ms").Should().BeTrue("the column and its rows are kept");
        registry.IsActive("dwell_ms").Should().BeFalse("ingestion refuses it and the query API does not offer it");
        registry.Active.Should().BeEmpty();
        registry.Declared.Should().ContainSingle();
    }

    [Fact]
    public void ConfigOrder_IsPreserved()
    {
        MeasureRegistry registry = Build([Valid("c"), Valid("a"), Valid("b")]);

        registry.Active.Select(m => m.Name).Should().ContainInOrder(
            new[] { "c", "a", "b" },
            "config order is insert order, and the writer pairs names against value arrays positionally");
    }

    [Fact]
    public void LookupIsCaseSensitive()
    {
        MeasureRegistry registry = Build([Valid("dwell_ms")]);

        registry.IsActive("dwell_ms").Should().BeTrue();
        registry.IsActive("Dwell_Ms").Should().BeFalse();
    }
}

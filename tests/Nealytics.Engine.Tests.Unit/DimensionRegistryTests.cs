using FluentAssertions;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// The registry is the only thing that decides which dimensions exist. Every rule it enforces is
/// enforced at boot, because the alternative to a refused boot is a service that reports healthy
/// and then does something silently wrong to the data.
/// </summary>
public class DimensionRegistryTests
{
    private static TelemetryEngineOptions Options(params DimensionOptions[] dimensions) =>
        new() { Dimensions = [.. dimensions] };

    private static DimensionOptions Dim(string name, string type = "String", bool retired = false) =>
        new() { Name = name, Type = type, Retired = retired };

    [Fact]
    public void EmptyDeclaration_IsValid_AndYieldsNoDimensions()
    {
        DimensionRegistry registry = new(Options());

        registry.Active.Should().BeEmpty("the shipped engine declares nothing");
        registry.Declared.Should().BeEmpty();
    }

    [Fact]
    public void Active_PreservesConfigOrder()
    {
        DimensionRegistry registry = new(Options(Dim("zzz"), Dim("aaa"), Dim("mmm")));

        // Config order is the insert order; re-sorting would make the column list unstable.
        registry.Active.Select(d => d.Name).Should().Equal(["zzz", "aaa", "mmm"]);
    }

    [Theory]
    [InlineData("String", "Nullable(String)")]
    [InlineData("LowCardinality", "LowCardinality(String)")]
    [InlineData("UInt64", "Nullable(UInt64)")]
    [InlineData("Int64", "Nullable(Int64)")]
    [InlineData("DateTime", "Nullable(DateTime64(3, 'UTC'))")]
    public void ClickHouseType_MatchesWhatSystemColumnsReports(string declared, string expected)
    {
        // These strings are compared verbatim against system.columns.type by the reconciler, so a
        // value that merely looks right turns every boot into a spurious type-mismatch refusal.
        DimensionRegistry registry = new(Options(Dim("d", declared)));

        registry.ClickHouseType("d").Should().Be(expected);
    }

    [Fact]
    public void LowCardinality_CarriesAnEmptyStringDefault()
    {
        DimensionRegistry registry = new(Options(Dim("plan_tier", "LowCardinality")));

        registry.Active[0].DefaultExpression.Should().Be("''",
            "a non-nullable LowCardinality column needs a default or the ADD COLUMN fails");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Article_Id")]
    [InlineData("9lives")]
    [InlineData("has-hyphen")]
    [InlineData("has space")]
    [InlineData("has;semicolon")]
    [InlineData("_leading")]
    public void InvalidName_RefusesToBuild(string name)
    {
        Action build = () => _ = new DimensionRegistry(Options(Dim(name)));

        build.Should().Throw<InvalidOperationException>().WithMessage("*not a valid column name*");
    }

    [Fact]
    public void NameLongerThanSixtyThreeCharacters_RefusesToBuild()
    {
        Action build = () => _ = new DimensionRegistry(Options(Dim("a" + new string('b', 63))));

        build.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DuplicateName_RefusesToBuild()
    {
        // Two buffers writing to one column: the second silently wins and the first is lost.
        Action build = () => _ = new DimensionRegistry(Options(Dim("widget_id"), Dim("widget_id")));

        build.Should().Throw<InvalidOperationException>().WithMessage("*a second time*");
    }

    [Theory]
    [InlineData("event_id")]
    [InlineData("project_id")]
    [InlineData("tenant_id")]
    [InlineData("session_id")]
    [InlineData("user_id")]
    [InlineData("event_type")]
    [InlineData("object_id")]
    [InlineData("device_class")]
    [InlineData("os")]
    [InlineData("browser")]
    [InlineData("country")]
    [InlineData("metadata_json")]
    [InlineData("timestamp")]
    public void ReservedCoreColumn_RefusesToBuild(string reserved)
    {
        // A dimension named `timestamp` generates DDL that either fails obscurely or shadows a
        // core column.
        Action build = () => _ = new DimensionRegistry(Options(Dim(reserved)));

        build.Should().Throw<InvalidOperationException>().WithMessage("*reserved core column*");
    }

    [Fact]
    public void UnknownType_RefusesToBuild_AndListsTheSupportedOnes()
    {
        Action build = () => _ = new DimensionRegistry(Options(Dim("widget_id", "Json")));

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown type 'Json'*")
            .WithMessage("*String*");
    }

    [Fact]
    public void MoreDimensionsThanMaxDimensions_RefusesToBuild()
    {
        // The registry issues DDL. A config bug must not be able to add columns without limit.
        TelemetryEngineOptions options = new()
        {
            MaxDimensions = 2,
            Dimensions = [Dim("a"), Dim("b"), Dim("c")],
        };

        Action build = () => _ = new DimensionRegistry(options);

        build.Should().Throw<InvalidOperationException>().WithMessage("*exceeding MaxDimensions of 2*");
    }

    [Fact]
    public void RetiredDimension_IsDeclaredButNotActive()
    {
        DimensionRegistry registry = new(Options(Dim("live_one"), Dim("old_one", retired: true)));

        registry.IsActive("old_one").Should().BeFalse("a retired dimension is refused on ingestion");
        registry.IsDeclared("old_one").Should().BeTrue(
            "it stays declared so the reconciler keeps its column and does not refuse the boot");
        registry.Active.Select(d => d.Name).Should().Equal("live_one");
        registry.Declared.Select(d => d.Name).Should().Equal("live_one", "old_one");
        registry.ClickHouseType("old_one").Should().BeNull("the query API must not offer it");
    }

    [Fact]
    public void IsActive_IsCaseSensitive()
    {
        // ClickHouse column names are case-sensitive; matching loosely here would accept a key
        // that then fails to bind at insert time.
        DimensionRegistry registry = new(Options(Dim("widget_id")));

        registry.IsActive("widget_id").Should().BeTrue();
        registry.IsActive("Widget_Id").Should().BeFalse();
    }
}

using FluentAssertions;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public class FilterParserTests
{
    private static (QueryColumns Columns, MeasureRegistry Measures) Registries()
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [new DimensionOptions { Name = "widget_id", Type = "String" }],
            Measures = [new MeasureOptions { Name = "result_count", Type = "UInt16", Aggregations = "sum" }],
        };
        DimensionRegistry dimensions = new(options);
        return (new QueryColumns(dimensions), new MeasureRegistry(options, dimensions));
    }

    private static FilterParser.Result Parse(params string[] raw)
    {
        (QueryColumns columns, MeasureRegistry measures) = Registries();
        return FilterParser.Parse(raw, columns, measures, 256);
    }

    [Fact]
    public void ADimensionFilterIsAnEqualityOnTheCanonicalColumn()
    {
        FilterParser.Result result = Parse("widget_id:w1", "page_path:/a?x=1:2");

        result.Outcome.Should().Be(FilterParser.Outcome.Ok);
        result.Filters.Should().HaveCount(2);
        result.Filters[0].IsMeasure.Should().BeFalse();
        result.Filters[0].Comparison.Should().Be(FilterComparison.Equal);
        result.Filters[1].Value.Should().Be("/a?x=1:2", "only the first colon separates");
    }

    [Theory]
    [InlineData("result_count=0", FilterComparison.Equal, "0")]
    [InlineData("result_count!=0", FilterComparison.NotEqual, "0")]
    [InlineData("result_count>3", FilterComparison.Greater, "3")]
    [InlineData("result_count>=3.5", FilterComparison.GreaterOrEqual, "3.5")]
    [InlineData("result_count<10", FilterComparison.Less, "10")]
    [InlineData("result_count<=10", FilterComparison.LessOrEqual, "10")]
    public void AMeasureFilterCarriesItsComparison(string raw, FilterComparison comparison, string value)
    {
        FilterParser.Result result = Parse(raw);

        result.Outcome.Should().Be(FilterParser.Outcome.Ok);
        result.Filters.Should().ContainSingle();
        result.Filters[0].IsMeasure.Should().BeTrue();
        result.Filters[0].Column.Should().Be("result_count");
        result.Filters[0].Comparison.Should().Be(comparison);
        result.Filters[0].Value.Should().Be(value);
    }

    [Theory]
    [InlineData("result_count>", FilterParser.Outcome.NotANumber)]
    [InlineData("result_count>abc", FilterParser.Outcome.NotANumber)]
    [InlineData("result_count>1e400", FilterParser.Outcome.NotANumber)]
    [InlineData("widget_id>3", FilterParser.Outcome.UnknownColumn)]
    [InlineData("nope>3", FilterParser.Outcome.UnknownColumn)]
    [InlineData("nope:x", FilterParser.Outcome.UnknownColumn)]
    [InlineData("result_count!5", FilterParser.Outcome.Malformed)]
    [InlineData("justtext", FilterParser.Outcome.Malformed)]
    [InlineData(":value", FilterParser.Outcome.Malformed)]
    public void WhatCannotBeReadIsNamed(string raw, FilterParser.Outcome expected)
    {
        Parse(raw).Outcome.Should().Be(expected);
    }

    [Fact]
    public void WithoutAMeasureRegistryAComparisonIsMalformed()
    {
        (QueryColumns columns, _) = Registries();

        FilterParser.Parse(["result_count>3"], columns, 256).Outcome.Should().Be(FilterParser.Outcome.Malformed);
    }

    [Fact]
    public void TheScopeClauseRendersBothKinds()
    {
        FilterParser.Result parsed = Parse("widget_id:w1", "result_count=0");
        QueryScope scope = new()
        {
            ProjectId = "p", TenantId = "t", From = DateTime.UnixEpoch, To = DateTime.UnixEpoch.AddDays(1),
            TrafficClass = "normal", Filters = parsed.Filters,
        };

        System.Text.StringBuilder sql = new();
        ScopeClause.AppendRaw(sql, scope);
        List<KeyValuePair<string, object?>> parameters = ScopeClause.Parameters(scope);

        sql.ToString().Should().Contain("toString(widget_id) = {filter0:String}");
        sql.ToString().Should().Contain("result_count = {filter1:Float64}");
        parameters.Should().Contain(p => p.Key == "filter1" && Equals(p.Value, 0d));
        ScopeClause.HasMeasureFilter(parsed.Filters).Should().BeTrue();
    }
}

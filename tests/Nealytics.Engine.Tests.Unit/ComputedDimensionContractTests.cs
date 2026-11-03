using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public sealed class ComputedDimensionContractTests
{
    [Fact]
    public void MetadataDefaultsAndAllowlistRejectionsAreCompleteAndDeterministic()
    {
        var options = new TelemetryEngineOptions();
        var dimensions = new DimensionRegistry(options);
        var columns = new QueryColumns(dimensions);
        string[] expected = ["browser", "country", "device_class", "event_type", "object_id", "os", "page_path", "referrer", "session_id", "tenant_id", "traffic_class", "user_id"];
        Assert.Equal(expected, columns.Allowed);
        Assert.Empty(columns.TenantGroupColumns);
        Assert.Throws<ArgumentNullException>(() => new QueryColumns(null!));
        foreach (string name in expected)
        {
            Assert.True(columns.TryResolve(name, out string resolved));
            Assert.Equal(name, resolved);
        }
        Assert.False(columns.TryResolve("missing", out string absent));
        Assert.Equal("", absent);
        Assert.False(columns.TryResolveNumeric("country", out absent));
        Assert.Equal("", absent);
        Assert.False(columns.TryResolveNumeric(null, out absent));
        Assert.Equal("", absent);
        string legacy = "'group' must be one of: " + string.Join(", ", expected)
            + ". Got 'missing', which is not a known column or an active dimension.";
        Assert.Equal(legacy, columns.RejectionMessage("group", "missing"));
        Assert.Equal(legacy, columns.GroupByRejectionMessage("group", "missing"));
        options.TenantAttributes = [new() { Name = "cohort" }, new() { Name = "zone" }];
        columns = new QueryColumns(dimensions, new TenantAttributeRegistry(options));
        Assert.Equal(new[] { "tenant.cohort", "tenant.zone" }, columns.TenantGroupColumns);
        Assert.True(columns.TryResolveGroupBy("tenant.cohort", out string group));
        Assert.Equal("tenant.cohort", group);
        Assert.False(columns.TryResolveGroupBy("tenant.missing", out group));
        Assert.Equal("", group);
        Assert.Equal("'group' must be one of: " + string.Join(", ", expected) + ", tenant.cohort, tenant.zone. "
            + "Got 'missing', which is not a known column, an active dimension or a declared tenant attribute.",
            columns.GroupByRejectionMessage("group", "missing"));
        var definition = new ComputedDimension("x", 1, "country", "lower", [], null, 1, 64, null);
        Assert.Equal("", definition.Expression);
        Assert.False(definition.Numeric);
        Assert.Equal(0, definition.Depth);
        Assert.Equal("", new ReportParameter("p", "country", "string", "eq", false, null).ResolvedColumn);
    }

    [Fact]
    public void CompiledMappingsAndBucketsKeepEverySqlDelimiterAndLiteral()
    {
        var definition = new ComputedDimension("x", 1, "country", "map",
            [new("a", "x"), new("b", "y")], "z", 1, 64, null);
        Assert.Equal("if(isNull(country), NULL, transform(assumeNotNull(toString(country)), [unhex('61'), unhex('62')], [unhex('78'), unhex('79')], unhex('7A')))",
            ComputedDimensionSql.Build(definition, "country"));
        Assert.Equal("if(isNull(country), NULL, transform(assumeNotNull(toString(country)), [unhex('61'), unhex('62')], [unhex('78'), unhex('79')], toString(country)))",
            ComputedDimensionSql.Build(definition with { Default = null }, "country"));
        Assert.Equal("if(isFinite(floor(toFloat64(amount) / toFloat64('10')) * toFloat64('10')), floor(toFloat64(amount) / toFloat64('10')) * toFloat64('10'), NULL)",
            ComputedDimensionSql.Build(definition with { Operation = "bucket", Width = 10 }, "amount"));
        Assert.Equal("Unknown compiled dimension operation.", Assert.Throws<InvalidOperationException>(() =>
            ComputedDimensionSql.Build(definition with { Operation = "invalid" }, "country")).Message);
    }

    [Fact]
    public void MappingFailuresKeepTheirDiagnostics()
    {
        var options = new TelemetryEngineOptions
        {
            ComputedDimensions = [new() { Name = "x", Source = "country", Operation = "map", Mapping = [null!] }]
        };
        void Build() => _ = new ComputedDimensionRegistry(options, new DimensionRegistry(options), new MeasureRegistry(options, new DimensionRegistry(options)));
        Assert.Equal("Computed mapping entry is null.", Assert.Throws<InvalidOperationException>(Build).Message);
        options.ComputedDimensions[0].Mapping = [new() { From = "x", To = null! }];
        Assert.Equal("Computed map keys must be unique and keys/values must not exceed 256 characters.", Assert.Throws<InvalidOperationException>(Build).Message);
    }

    [Theory]
    [InlineData(":x", FilterParser.Outcome.Malformed, "")]
    [InlineData("=1", FilterParser.Outcome.Malformed, "")]
    [InlineData("country:x", FilterParser.Outcome.Ok, "")]
    [InlineData("!country:x", FilterParser.Outcome.Malformed, "")]
    [InlineData("amount!", FilterParser.Outcome.Malformed, "")]
    [InlineData("amount=", FilterParser.Outcome.NotANumber, "amount")]
    [InlineData("amount=10", FilterParser.Outcome.Ok, "")]
    [InlineData("amount<0", FilterParser.Outcome.Ok, "")]
    [InlineData("amount=123", FilterParser.Outcome.NotANumber, "amount")]
    public void FilterFailuresAndBoundaryNumbersKeepAllResultFields(string text, FilterParser.Outcome outcome, string column)
    {
        var options = new TelemetryEngineOptions { Measures = [new() { Name = "amount", Type = "Float64" }] };
        var dimensions = new DimensionRegistry(options);
        var columns = new QueryColumns(dimensions);
        var measures = new MeasureRegistry(options, dimensions);
        Assert.Throws<ArgumentNullException>(() => FilterParser.Parse(null!, columns, measures, 2));
        Assert.Throws<ArgumentNullException>(() => FilterParser.Parse([], null!, measures, 2));
        var result = FilterParser.Parse([" ", text], columns, measures, 2);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(column, result.Column);
        Assert.Equal(outcome == FilterParser.Outcome.Ok ? "" : text, result.Offender);
        if (outcome == FilterParser.Outcome.Ok)
        {
            var filter = Assert.Single(result.Filters);
            Assert.Equal(text[(text.IndexOfAny([':', '=', '<']) + 1)..], filter.Value);
        }
        else
        {
            Assert.Empty(result.Filters);
        }
    }
}

using System.Text;
using System.Text.Json;
using Nealytics.Engine.Features.GetSavedReport;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed class SavedReportShapeTests
{
    private static TelemetryEngineOptions Options()
    {
        var options = SavedReportRegistryTests.Options();
        options.SavedReports[0].Parameters =
        [
            new() { Name = "location", Column = "country", Default = "GB" },
            new() { Name = "threshold", Column = "amount", Type = "number", Comparison = "gte", Required = true }
        ];
        return options;
    }

    private static SavedReportRequestResult Request(
        TelemetryEngineOptions options, IReadOnlyDictionary<string, string[]>? values = null,
        string[]? filters = null, string name = "overview")
    {
        var dimensions = new DimensionRegistry(options);
        var columns = new QueryColumns(dimensions);
        var measures = new MeasureRegistry(options, dimensions);
        var metrics = new NamedMetricRegistry(options, measures);
        var segments = new EventSegmentRegistry(options, columns, measures);
        return SavedReportRequestFactory.Create(new ReadIdentity { ProjectId = "p", TenantId = "t" }, name,
            null, null, null, filters ?? [], new SavedReportRegistry(options, metrics, segments), metrics,
            segments, columns, measures, 24, new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), values);
    }

    [Fact]
    public void ParametersCompileIntoImmutableCanonicalTypedMetadata()
    {
        var options = Options();
        var configured = options.SavedReports[0];
        configured.GroupBy = [" unit_key ", "country"];
        configured.Interval = " HOUR ";
        configured.Limit = 7;
        configured.Parameters[0].Name = " location ";
        configured.Parameters[0].Column = " country ";
        configured.Parameters[0].Type = " STRING ";
        configured.Parameters[0].Comparison = " EQ ";
        var report = SavedReportRegistryTests.Build(options).Find("overview")!;
        Assert.Equal(new[] { "unit_key", "country" }, report.GroupBy);
        Assert.Equal("hour", report.Interval);
        Assert.Equal(7, report.Limit);
        Assert.True(report.IsGrouped);
        Assert.Equal(new ReportParameter("location", "country", "string", "eq", false, "GB") { ResolvedColumn = "country" }, report.Parameters[0]);
        Assert.Equal(FilterComparison.GreaterOrEqual, report.Parameters[1].Predicate);
        Assert.Equal("number", report.Parameters[1].Type);
        Assert.True(report.Parameters[1].Required);
        configured.GroupBy.Clear();
        configured.Parameters[0].Default = "changed";
        configured.Parameters.Clear();
        Assert.Equal(2, report.GroupBy.Count);
        Assert.Equal("GB", report.Parameters[0].Default);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)report.GroupBy).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ReportParameter>)report.Parameters).Clear());
        var defaults = new ReportParameterOptions();
        Assert.Equal("", defaults.Name);
        Assert.Equal("", defaults.Column);
        Assert.Equal("string", defaults.Type);
        Assert.Equal("eq", defaults.Comparison);
        Assert.False(defaults.Required);
        Assert.Null(defaults.Default);
        Assert.Empty(new SavedReportOptions().GroupBy);
        Assert.Empty(new SavedReportOptions().Parameters);
        Assert.Equal("", new SavedReportOptions().Interval);
        Assert.Equal(100, new SavedReportOptions().Limit);
    }

    [Theory]
    [InlineData(0, "nonnull list")]
    [InlineData(1, "nonnull list")]
    [InlineData(2, "unique active")]
    [InlineData(3, "unique active")]
    [InlineData(4, "unique active")]
    [InlineData(5, "interval must")]
    [InlineData(6, "row or 16384-cell")]
    [InlineData(7, "row or 16384-cell")]
    [InlineData(8, "row or 16384-cell")]
    [InlineData(9, "row or 16384-cell")]
    public void InvalidShapesAreRejectedBeforeActivation(int invalidCase, string diagnostic)
    {
        var options = Options();
        var report = options.SavedReports[0];
        report.GroupBy = ["country"];
        switch (invalidCase)
        {
            case 0: report.GroupBy = null!; break;
            case 1: report.GroupBy = ["country", "page_path", "os"]; break;
            case 2: report.GroupBy = ["country", " country "]; break;
            case 3: report.GroupBy = [null!]; break;
            case 4: report.GroupBy = ["tenant.cohort"]; break;
            case 5: report.Interval = "minute"; break;
            case 6: report.Limit = 0; break;
            case 7: report.Limit = -1; break;
            case 8: report.Limit = 10_001; break;
            case 9: options.MaxQueryLimit = 99; break;
        }
        Assert.Contains(diagnostic, Assert.Throws<InvalidOperationException>(() => SavedReportRegistryTests.Build(options)).Message);
    }

    [Fact]
    public void RowAndCellLimitsIncludeTheirExactEndpoints()
    {
        var options = SavedReportRegistryTests.Options();
        var report = options.SavedReports[0];
        report.GroupBy = ["country"];
        foreach (int limit in new[] { 1, 10_000 })
        {
            report.Limit = limit;
            Assert.Equal(limit, SavedReportRegistryTests.Build(options).Find("overview")!.Limit);
        }
        report.Fields = Enumerable.Range(0, 16).Select(index => new SavedReportFieldOptions { Name = "f" + index, Metric = "activity" }).ToList();
        report.Limit = 1024;
        Assert.Equal(1024, SavedReportRegistryTests.Build(options).Find("overview")!.Limit);
        report.Limit++;
        Assert.Contains("16384-cell", Assert.Throws<InvalidOperationException>(() => SavedReportRegistryTests.Build(options)).Message);
        report.GroupBy.Clear();
        report.Limit = 10_000;
        Assert.False(SavedReportRegistryTests.Build(options).Find("overview")!.IsGrouped);
        report.Interval = "hour";
        report.Limit = 1024;
        Assert.True(SavedReportRegistryTests.Build(options).Find("overview")!.IsGrouped);
        report.Limit++;
        Assert.Throws<InvalidOperationException>(() => SavedReportRegistryTests.Build(options));
        report.Interval = "";
        report.Limit = -1;
        Assert.Throws<InvalidOperationException>(() => SavedReportRegistryTests.Build(options));
        report.Limit = 100;
        Assert.False(SavedReportRegistryTests.Build(options).Find("overview")!.IsGrouped);
    }

    [Theory]
    [InlineData(0, "nonnull list")]
    [InlineData(1, "16 declarations")]
    [InlineData(2, "declaration is null")]
    [InlineData(3, "names must")]
    [InlineData(4, "names must")]
    [InlineData(5, "names must")]
    [InlineData(6, "names must")]
    [InlineData(7, "type must match")]
    [InlineData(8, "type must match")]
    [InlineData(9, "type must match")]
    [InlineData(10, "type must match")]
    [InlineData(11, "type must match")]
    [InlineData(12, "comparison is unsupported")]
    [InlineData(13, "comparison is unsupported")]
    [InlineData(14, "default must")]
    [InlineData(15, "type must match")]
    public void InvalidParameterDeclarationsAreRejected(int invalidCase, string diagnostic)
    {
        var options = Options();
        var report = options.SavedReports[0];
        var parameter = report.Parameters[0];
        switch (invalidCase)
        {
            case 0: report.Parameters = null!; break;
            case 1: report.Parameters = Enumerable.Range(0, 17).Select(index => new ReportParameterOptions { Name = "p" + index, Column = "country" }).ToList(); break;
            case 2: report.Parameters[0] = null!; break;
            case 3: parameter.Name = null!; break;
            case 4: parameter.Name = "Upper"; break;
            case 5: parameter.Name = new string('p', 64); break;
            case 6: report.Parameters.Add(new() { Name = " location ", Column = "country" }); break;
            case 7: parameter.Column = null!; break;
            case 8: parameter.Column = "missing"; break;
            case 9: parameter.Type = null!; break;
            case 10: parameter.Type = "bool"; break;
            case 11: parameter.Type = "number"; break;
            case 12: parameter.Comparison = null!; break;
            case 13: parameter.Comparison = "ne"; break;
            case 14: parameter.Default = " "; break;
            case 15: parameter.Type = "number"; parameter.Column = null!; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => SavedReportRegistryTests.Build(options));
        Assert.StartsWith("TelemetryEngine:SavedReports[0]: ", error.Message);
        Assert.Contains(diagnostic, error.Message);
    }

    [Theory]
    [InlineData("eq", FilterComparison.Equal)]
    [InlineData("ne", FilterComparison.NotEqual)]
    [InlineData("lt", FilterComparison.Less)]
    [InlineData("lte", FilterComparison.LessOrEqual)]
    [InlineData("gt", FilterComparison.Greater)]
    [InlineData("gte", FilterComparison.GreaterOrEqual)]
    public void NumericParametersUseOnlySupportedComparisons(string comparison, FilterComparison expected)
    {
        var options = Options();
        options.SavedReports[0].Parameters[1].Comparison = comparison;
        var result = Request(options, new Dictionary<string, string[]> { ["threshold"] = ["1.25e2"] });
        Assert.Equal(200, result.StatusCode);
        var filter = result.Request!.Scope.Filters[1];
        Assert.Equal("amount", filter.Column);
        Assert.True(filter.IsMeasure);
        Assert.Equal(expected, filter.Comparison);
        Assert.Equal("1.25e2", filter.Value);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("nan", false)]
    [InlineData("inf", false)]
    [InlineData("-Infinity", false)]
    [InlineData("1e309", false)]
    [InlineData("1,2", false)]
    [InlineData("1.25", true)]
    [InlineData("-1e308", true)]
    [InlineData("1e-308", true)]
    public void NumericDefaultsAndInputsMustBeFinite(string value, bool valid)
    {
        var options = Options();
        options.SavedReports[0].Parameters[1].Default = value;
        if (valid)
        {
            Assert.Equal(200, Request(options).StatusCode);
        }
        else
        {
            Assert.Contains("default must", Assert.Throws<InvalidOperationException>(() => SavedReportRegistryTests.Build(options)).Message);
            options.SavedReports[0].Parameters[1].Default = null;
            Assert.Equal(400, Request(options, new Dictionary<string, string[]> { ["threshold"] = [value] }).StatusCode);
        }
    }

    [Fact]
    public void ParameterCountNamesAndValueLengthsIncludeTheirExactBounds()
    {
        var options = Options();
        options.SavedReports[0].Parameters = Enumerable.Range(0, 16).Select(index => new ReportParameterOptions
        {
            Name = index == 0 ? "p" : index == 15 ? new string('p', 63) : "p" + index,
            Column = "country", Default = new string('x', 256)
        }).ToList();
        Assert.Equal(16, SavedReportRegistryTests.Build(options).Find("overview")!.Parameters.Count);
        Assert.Equal(200, Request(options).StatusCode);
        options.SavedReports[0].Parameters[0].Default += "x";
        Assert.Throws<InvalidOperationException>(() => SavedReportRegistryTests.Build(options));
        Assert.False(ReportParameterBinding.IsValidValue("unknown", "1"));
    }

    [Fact]
    public void BindingRejectsUnknownRepeatedMissingAndInvalidValuesBeforeStorage()
    {
        var options = Options();
        Assert.Equal("Report parameter 'threshold' is required.", Request(options).Error);
        Assert.Equal("Unknown report parameter 'missing'.", Request(options,
            new Dictionary<string, string[]> { ["missing"] = ["x"] }).Error);
        foreach (string[]? values in new string[]?[] { null, [], ["1", "2"], [null!] })
        {
            Assert.Equal("Report parameter 'threshold' requires exactly one value.", Request(options,
                new Dictionary<string, string[]> { ["threshold"] = values! }).Error);
        }
        Assert.Equal(400, Request(options, new Dictionary<string, string[]> { ["threshold"] = ["1"], ["location"] = [new string('x', 257)] }).StatusCode);
        Assert.Equal("Report parameter 'threshold' requires a nonblank value of at most 256 characters matching its type.",
            Request(options, new Dictionary<string, string[]> { ["threshold"] = ["nan"] }).Error);
        Assert.Equal(404, Request(options, name: "missing").StatusCode);
        options.SavedReports[0].Parameters.Clear();
        Assert.Equal(200, Request(options).StatusCode);
    }

    [Fact]
    public void BoundDefaultsAndCallerFiltersAreAndedAndRemainSeparateFromSql()
    {
        var options = Options();
        options.SavedReports[0].Parameters.Add(new() { Name = "optional", Column = "page_path" });
        options.EventSegments = [new() { Name = "selected", Filters = ["browser:x"] }];
        options.SavedReports[0].Segment = "selected";
        var request = Request(options, new Dictionary<string, string[]>
        {
            ["threshold"] = ["2"], ["location"] = ["x' OR 1=1"]
        }, ["os:linux"]).Request!;
        Assert.Equal(new[] { "x", "linux", "x' OR 1=1", "2" }, request.Scope.Filters.Select(filter => filter.Value));
        var query = GetSavedReportQuery.BuildQuery(request);
        Assert.DoesNotContain("x' OR", query.Sql);
        Assert.Contains(query.Parameters, pair => Equals(pair.Value, "x' OR 1=1"));
        Assert.Null(SavedReportRequestFactory.UnsupportedParameter(["param.location", "from"], request.Definition.Parameters));
        Assert.NotNull(SavedReportRequestFactory.UnsupportedParameter(["param.missing"], request.Definition.Parameters));
        Assert.NotNull(SavedReportRequestFactory.UnsupportedParameter(["xxxxxxlocation"], request.Definition.Parameters));
        Assert.NotNull(SavedReportRequestFactory.UnsupportedParameter(["x"], request.Definition.Parameters));
        Assert.NotNull(SavedReportRequestFactory.UnsupportedParameter(["param.location"]));
    }

    [Fact]
    public void ParametersAndDeclaredGroupingBothParticipateInUnitSafety()
    {
        var options = SavedReportRegistryTests.Options();
        options.NamedMetrics[0].Aggregation = "sum";
        options.NamedMetrics[0].Measure = "amount";
        Assert.Equal(400, Request(options).StatusCode);
        options.SavedReports[0].Parameters = [new() { Name = "unit", Column = "unit_key", Default = "u" }];
        Assert.Equal(200, Request(options).StatusCode);
        options.SavedReports[0].Parameters.Clear();
        options.SavedReports[0].GroupBy = ["country", "unit_key"];
        Assert.Equal(200, Request(options).StatusCode);
        options.SavedReports[0].GroupBy = ["country"];
        Assert.Equal(400, Request(options).StatusCode);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("hour", "toStartOfHour(toTimeZone(timestamp, 'UTC')), ")]
    [InlineData("day", "toStartOfDay(toTimeZone(timestamp, 'UTC')), ")]
    public void ShapeSqlUsesDeclaredKeysAndDeterministicOrdering(string? interval, string bucket)
    {
        var options = SavedReportRegistryTests.Options();
        options.SavedReports[0].GroupBy = ["country", "unit_key"];
        options.SavedReports[0].Interval = interval!;
        options.SavedReports[0].Limit = 2;
        var query = GetSavedReportQuery.BuildQuery(Request(options).Request!);
        Assert.StartsWith("SELECT " + bucket + "toString(country), toString(unit_key), toString(count()) FROM", query.Sql);
        Assert.Contains(interval is null ? " GROUP BY 1, 2 ORDER BY 1 ASC NULLS FIRST, 2 ASC NULLS FIRST LIMIT 2"
            : " GROUP BY 1, 2, 3 ORDER BY 1 ASC NULLS FIRST, 2 ASC NULLS FIRST, 3 ASC NULLS FIRST LIMIT 2", query.Sql);
        Assert.Contains("SETTINGS enable_positional_arguments=1", query.Sql);
        Assert.Equal(interval is not null, query.Sql.Contains("enable_extended_results_for_datetime_functions=1"));
        Assert.Equal("Unknown compiled report interval.", Assert.Throws<InvalidOperationException>(() => ReportShapeSql.Bucket("bad")).Message);
    }

    [Fact]
    public void ScalarAndBucketOnlyShapesHaveNoSpuriousKeys()
    {
        var options = SavedReportRegistryTests.Options();
        var scalar = GetSavedReportQuery.BuildQuery(Request(options).Request!);
        Assert.DoesNotContain("GROUP BY", scalar.Sql);
        options.SavedReports[0].Interval = "hour";
        var series = GetSavedReportQuery.BuildQuery(Request(options).Request!);
        Assert.Contains(" GROUP BY 1 ORDER BY 1 ASC NULLS FIRST LIMIT 100", series.Sql);
        var sql = new StringBuilder();
        ReportShapeSql.AppendKeys(sql, SavedReportRegistryTests.Build(SavedReportRegistryTests.Options()).Find("overview")!);
        Assert.Equal("", sql.ToString());
    }

    [Fact]
    public void RowJsonPreservesNullGroupValuesAndScalarDefaults()
    {
        var scalar = new SavedReportResponse();
        Assert.Equal("scalar", scalar.Shape);
        Assert.Empty(scalar.Rows);
        var row = new SavedReportRow();
        Assert.Null(row.Bucket);
        Assert.Empty(row.Groups);
        Assert.Empty(row.Fields);
        var group = new SavedReportGroup();
        Assert.Equal("", group.Name);
        Assert.Null(group.Value);
        var response = new SavedReportResponse
        {
            Shape = "table", Rows = [new() { Groups = [new() { Name = "country", Value = null }] }]
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, TelemetryAotContext.Default.SavedReportResponse));
        Assert.Equal("table", document.RootElement.GetProperty("shape").GetString());
        var serialized = document.RootElement.GetProperty("rows")[0].GetProperty("groups")[0];
        Assert.Equal("country", serialized.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, serialized.GetProperty("value").ValueKind);
    }
}

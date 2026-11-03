using System.Text.Json;
using Nealytics.Engine.Features.GetNamedMetric;
using Nealytics.Engine.Features.GetSavedReport;
using Nealytics.Engine.Features.GetSchema;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed class ComputedDimensionTests
{
    private static TelemetryEngineOptions Options() => NamedMetricRegistryTests.Options();

    private static ComputedDimensionRegistry Build(TelemetryEngineOptions options)
    {
        var dimensions = new DimensionRegistry(options);
        return new ComputedDimensionRegistry(options, dimensions, new MeasureRegistry(options, dimensions));
    }

    private static ComputedDimensionOptions Declaration(string name = "normalized") =>
        new() { Name = name, Source = "country", Operation = "lower" };

    [Fact]
    public void DefaultsAndMissingConfigurationKeepTheLegacyAllowlistUnchanged()
    {
        var options = Options();
        Assert.Equal(32, options.MaxComputedDimensions);
        Assert.Empty(options.ComputedDimensions);
        Assert.Empty(Build(options).Declared);
        options.ComputedDimensions = null!;
        Assert.Empty(Build(options).Declared);
        var declaration = new ComputedDimensionOptions();
        Assert.Equal("", declaration.Name);
        Assert.Equal(1, declaration.Version);
        Assert.Equal("", declaration.Source);
        Assert.Equal("", declaration.Operation);
        Assert.Empty(declaration.Mapping);
        Assert.Null(declaration.Default);
        Assert.Equal(1, declaration.Start);
        Assert.Equal(64, declaration.Length);
        Assert.Null(declaration.Width);
        var mapping = new DimensionMappingOptions();
        Assert.Equal("", mapping.From);
        Assert.Equal("", mapping.To);
        options.ComputedDimensions = [Declaration()];
        var dimensions = new DimensionRegistry(options);
        Assert.False(new QueryColumns(dimensions).TryResolve("normalized", out _));
        var extended = new QueryColumns(dimensions, null, Build(options));
        Assert.Equal("lowerUTF8(toString(country))", extended.ExpressionFor("normalized"));
        Assert.Contains("normalized", extended.Allowed);
        Assert.Same(extended, new AnalyticsColumns(extended).Value);
        Assert.Equal("columns", Assert.Throws<ArgumentNullException>(() => new AnalyticsColumns(null!)).ParamName);
    }

    [Fact]
    public void EveryDependencyIsRequiredEvenWhenNoDefinitionsAreConfigured()
    {
        var options = Options();
        var dimensions = new DimensionRegistry(options);
        var measures = new MeasureRegistry(options, dimensions);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => new ComputedDimensionRegistry(null!, dimensions, measures)).ParamName);
        Assert.Equal("dimensions", Assert.Throws<ArgumentNullException>(() => new ComputedDimensionRegistry(options, null!, measures)).ParamName);
        Assert.Equal("measures", Assert.Throws<ArgumentNullException>(() => new ComputedDimensionRegistry(options, dimensions, null!)).ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(129)]
    public void InvalidCapacityCannotActivate(int capacity)
    {
        var options = Options();
        options.MaxComputedDimensions = capacity;
        Assert.Equal("MaxComputedDimensions must be between 1 and 128.", Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(128)]
    public void ExactCapacitiesCompileWithoutAccumulatingDependencyDepth(int capacity)
    {
        var options = Options();
        options.MaxComputedDimensions = capacity;
        options.ComputedDimensions = Enumerable.Range(0, capacity).Select(index => Declaration("d" + index)).ToList();
        Assert.Equal(capacity, Build(options).Declared.Count);
        options.ComputedDimensions.Add(Declaration("extra"));
        Assert.Equal("ComputedDimensions exceeds MaxComputedDimensions.", Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Upper")]
    [InlineData("1name")]
    [InlineData("a-b")]
    [InlineData("timestamp")]
    [InlineData("country")]
    [InlineData("unit_key")]
    [InlineData("amount")]
    public void NamesCannotShadowPhysicalFieldsOrUseInvalidGrammar(string? name)
    {
        var options = Options();
        options.ComputedDimensions = [Declaration(name!)];
        Assert.Contains("names must be unique", Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Fact]
    public void DeclarationNullsDuplicatesAndNameLengthsHaveExplicitBoundaries()
    {
        var options = Options();
        options.ComputedDimensions = [null!];
        Assert.Throws<InvalidOperationException>(() => Build(options));
        options.ComputedDimensions = [Declaration("a"), Declaration(" a ")];
        Assert.Throws<InvalidOperationException>(() => Build(options));
        foreach (int length in new[] { 1, 63 })
        {
            options.ComputedDimensions = [Declaration(" " + new string('a', length) + " ")];
            Assert.Equal(new string('a', length), Assert.Single(Build(options).Declared).Name);
        }
        NamesCannotShadowPhysicalFieldsOrUseInvalidGrammar(new string('a', 64));
        options.Measures[0].UnitDimension = "";
        options.Dimensions[0].Retired = true;
        options.ComputedDimensions = [Declaration("unit_key")];
        Assert.Throws<InvalidOperationException>(() => Build(options));
        options.Measures[0].Retired = true;
        options.ComputedDimensions = [Declaration("amount")];
        Assert.Throws<InvalidOperationException>(() => Build(options));
    }

    [Theory]
    [InlineData(0, "version 1")]
    [InlineData(1, "version 1")]
    [InlineData(2, "version 1")]
    [InlineData(3, "version 1")]
    [InlineData(4, "active event field")]
    [InlineData(5, "active event field")]
    [InlineData(6, "active event field")]
    [InlineData(7, "source type")]
    [InlineData(8, "finite positive width")]
    [InlineData(9, "finite positive width")]
    [InlineData(10, "substring settings")]
    [InlineData(11, "substring settings")]
    [InlineData(12, "mapping/default settings")]
    [InlineData(13, "mapping/default settings")]
    public void InvalidOperationsAndSettingsFailAtActivation(int invalidCase, string diagnostic)
    {
        var options = Options();
        var declaration = Declaration();
        options.ComputedDimensions = [declaration];
        switch (invalidCase)
        {
            case 0: declaration.Version = 0; break;
            case 1: declaration.Version = 2; break;
            case 2: declaration.Operation = null!; break;
            case 3: declaration.Operation = "sql"; break;
            case 4: declaration.Source = null!; break;
            case 5: declaration.Source = "missing"; break;
            case 6: declaration.Source = "country) OR 1=1"; break;
            case 7: declaration.Operation = "bucket"; declaration.Width = 10; break;
            case 8: declaration.Operation = "bucket"; declaration.Source = "amount"; break;
            case 9: declaration.Width = 10; break;
            case 10: declaration.Start = 2; break;
            case 11: declaration.Length = 65; break;
            case 12: declaration.Default = "x"; break;
            case 13: declaration.Mapping = [new() { From = "x", To = "y" }]; break;
        }
        Assert.Contains(diagnostic, Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Theory]
    [InlineData("lower", "lowerUTF8(toString(country))")]
    [InlineData("upper", "upperUTF8(toString(country))")]
    [InlineData("trim", "trimBoth(toString(country))")]
    [InlineData("substring", "substringUTF8(toString(country), 1, 64)")]
    public void StringOperatorsUseOnlyFixedFunctions(string operation, string expected)
    {
        var options = Options();
        var declaration = Declaration();
        declaration.Operation = " " + operation.ToUpperInvariant() + " ";
        declaration.Source = " country ";
        options.ComputedDimensions = [declaration];
        var definition = Assert.Single(Build(options).Declared);
        Assert.Equal(operation, definition.Operation);
        Assert.Equal("country", definition.Source);
        Assert.Equal(1, definition.Version);
        Assert.Equal(1, definition.Depth);
        Assert.False(definition.Numeric);
        Assert.Equal(expected, definition.Expression);
        Assert.Throws<InvalidOperationException>(() => ComputedDimensionSql.Build(definition with { Operation = "bad" }, "country"));
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(256, 256, true)]
    [InlineData(0, 1, false)]
    [InlineData(257, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 257, false)]
    public void SubstringBoundsIncludeTheirEndpoints(int start, int length, bool valid)
    {
        var options = Options();
        options.ComputedDimensions = [new() { Name = "part", Source = "country", Operation = "substring", Start = start, Length = length }];
        if (valid)
        {
            var definition = Assert.Single(Build(options).Declared);
            Assert.Equal(start, definition.Start);
            Assert.Equal(length, definition.Length);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => Build(options));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void BucketWidthsMustBeFiniteAndPositive(double width)
    {
        var options = Options();
        options.ComputedDimensions = [new() { Name = "band", Source = "amount", Operation = "bucket", Width = width }];
        Assert.Contains("finite positive width", Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Fact]
    public void NumericSourcesAndChainedBucketsKeepTheirTypesAndUseFiniteResults()
    {
        var options = Options();
        options.Dimensions.Add(new() { Name = "sequence", Type = "UInt64" });
        options.ComputedDimensions =
        [
            new() { Name = "band", Source = "amount", Operation = "bucket", Width = 10 },
            new() { Name = "band_again", Source = "band", Operation = "bucket", Width = 100 },
            new() { Name = "number_band", Source = "sequence", Operation = "bucket", Width = 1 },
            new() { Name = "label", Source = "band", Operation = "upper" }
        ];
        var registry = Build(options);
        var definition = registry.Find("band")!;
        Assert.True(definition.Numeric);
        Assert.Equal(10, definition.Width);
        Assert.Equal("if(isFinite(floor(toFloat64(amount) / toFloat64('10')) * toFloat64('10')), floor(toFloat64(amount) / toFloat64('10')) * toFloat64('10'), NULL)", definition.Expression);
        Assert.Equal(2, registry.Find("band_again")!.Depth);
        Assert.False(registry.Find("label")!.Numeric);
        var columns = new QueryColumns(new DimensionRegistry(options), null, registry);
        Assert.True(columns.TryResolveNumeric("band", out string expression));
        Assert.Equal(definition.Expression, expression);
        Assert.False(columns.TryResolveNumeric("label", out _));
        Assert.False(columns.TryResolveNumeric(null, out _));
        var measures = new MeasureRegistry(options, new DimensionRegistry(options));
        var parsed = FilterParser.Parse(["band>=10"], columns, measures, 256);
        Assert.Equal(FilterParser.Outcome.Ok, parsed.Outcome);
        Assert.Equal(definition.Expression, Assert.Single(parsed.Filters).Column);
        Assert.True(parsed.Filters[0].IsMeasure);
        options.ComputedDimensions.Add(new() { Name = "invalid", Source = "label", Operation = "bucket", Width = 10 });
        Assert.Throws<InvalidOperationException>(() => Build(options));
    }

    [Fact]
    public void MapsTreatConfigurationAsUtf8DataAndFreezeTheirSnapshots()
    {
        var options = Options();
        var declaration = new ComputedDimensionOptions
        {
            Name = "mapped", Source = "country", Operation = "map",
            Mapping = [new() { From = "x'\\\n", To = "é;SELECT" }], Default = "fallback"
        };
        options.ComputedDimensions = [declaration];
        var registry = Build(options);
        var definition = Assert.Single(registry.Declared);
        Assert.Same(definition, registry.Find("mapped"));
        Assert.Null(registry.Find(null));
        Assert.Null(registry.Find("Mapped"));
        Assert.Contains("unhex('78275C0A')", definition.Expression);
        Assert.Contains("unhex('C3A93B53454C454354')", definition.Expression);
        Assert.Contains("unhex('66616C6C6261636B')", definition.Expression);
        Assert.DoesNotContain("SELECT", definition.Expression);
        Assert.Equal("fallback", definition.Default);
        declaration.Mapping[0].From = "changed";
        declaration.Mapping.Clear();
        options.ComputedDimensions.Clear();
        Assert.Equal("x'\\\n", definition.Mapping[0].From);
        Assert.Equal("é;SELECT", definition.Mapping[0].To);
        Assert.Throws<NotSupportedException>(() => ((IList<DimensionMapping>)definition.Mapping).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ComputedDimension>)registry.Declared).Clear());
        declaration.Mapping = [new() { From = "", To = "" }];
        declaration.Default = null;
        options.ComputedDimensions.Add(declaration);
        Assert.EndsWith(", toString(country)))", Assert.Single(Build(options).Declared).Expression);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void InvalidMappingDeclarationsFailBeforeSqlCanRun(int invalidCase)
    {
        var options = Options();
        var declaration = new ComputedDimensionOptions { Name = "mapped", Source = "country", Operation = "map", Mapping = [new() { From = "x", To = "y" }] };
        options.ComputedDimensions = [declaration];
        switch (invalidCase)
        {
            case 0: declaration.Mapping = null!; break;
            case 1: declaration.Mapping.Clear(); break;
            case 2: declaration.Mapping = Enumerable.Range(0, 33).Select(index => new DimensionMappingOptions { From = "k" + index, To = "v" }).ToList(); break;
            case 3: declaration.Default = new string('x', 257); break;
            case 4: declaration.Mapping[0] = null!; break;
            case 5: declaration.Mapping[0].From = null!; break;
            case 6: declaration.Mapping[0].To = new string('x', 257); break;
            case 7: declaration.Mapping.Add(new() { From = "x", To = "other" }); break;
        }
        Assert.Throws<InvalidOperationException>(() => Build(options));
    }

    [Fact]
    public void ForwardReferencesCyclesAndDepthAreValidatedInEitherDeclarationOrder()
    {
        var options = Options();
        options.ComputedDimensions = [new() { Name = "first", Source = "second", Operation = "upper" }, Declaration("second")];
        var registry = Build(options);
        Assert.Equal(new[] { "first", "second" }, registry.Declared.Select(definition => definition.Name));
        Assert.Equal("upperUTF8(toString(lowerUTF8(toString(country))))", registry.Find("first")!.Expression);
        options.ComputedDimensions[1].Source = "first";
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
        options.ComputedDimensions = [new() { Name = "first", Source = "first", Operation = "trim" }];
        Assert.Throws<InvalidOperationException>(() => Build(options));
        for (int size = 8; size <= 9; size++)
        {
            options.ComputedDimensions = Enumerable.Range(0, size).Select(index => new ComputedDimensionOptions
            {
                Name = "d" + index, Source = index == size - 1 ? "country" : "d" + (index + 1), Operation = "lower"
            }).ToList();
            foreach (bool reverse in new[] { false, true })
            {
                if (reverse)
                {
                    options.ComputedDimensions.Reverse();
                }
                if (size == 8)
                {
                    Assert.Equal(8, Build(options).Find("d0")!.Depth);
                }
                else
                {
                    Assert.Throws<InvalidOperationException>(() => Build(options));
                }
            }
        }
    }

    [Fact]
    public void ComputedParametersAndGroupingUseExpressionsWithoutExposingThemAsNames()
    {
        var options = Options();
        options.ComputedDimensions = [Declaration(), new() { Name = "band", Source = "amount", Operation = "bucket", Width = 10 }];
        options.SavedReports = [new()
        {
            Name = "overview", Fields = [new() { Name = "events", Metric = "activity" }], GroupBy = ["normalized"],
            Parameters = [new() { Name = "location", Column = "normalized", Default = "gb" }, new() { Name = "minimum", Column = "band", Type = "number", Default = "10", Comparison = "gte" }]
        }];
        var report = SavedReportRegistryTests.Build(options).Find("overview")!;
        Assert.Equal("normalized", Assert.Single(report.GroupBy));
        Assert.Equal("lowerUTF8(toString(country))", Assert.Single(report.GroupExpressions));
        Assert.Equal("normalized", report.Parameters[0].Column);
        var bound = ReportParameterBinding.Bind(report.Parameters, null);
        Assert.Null(bound.Error);
        Assert.Equal("lowerUTF8(toString(country))", bound.Filters[0].Column);
        Assert.True(bound.Filters[1].IsMeasure);
        var query = GetSavedReportQuery.BuildQuery(new SavedReportRequest(report, new QueryScope
        {
            ProjectId = "p", TenantId = "t", Filters = bound.Filters
        }));
        Assert.StartsWith("SELECT toString(lowerUTF8(toString(country))),", query.Sql);
        var schema = new SchemaResponse { ComputedDimensions = Build(options).Declared };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(schema, TelemetryAotContext.Default.SchemaResponse));
        var serialized = document.RootElement.GetProperty("computedDimensions")[0];
        Assert.Equal("normalized", serialized.GetProperty("name").GetString());
        Assert.Equal("lower", serialized.GetProperty("operation").GetString());
        Assert.False(serialized.TryGetProperty("expression", out _));
        Assert.False(serialized.TryGetProperty("numeric", out _));
        Assert.False(serialized.TryGetProperty("depth", out _));
    }

    [Fact]
    public void ExpansionBudgetIncludesItsEndpointAndAccountsForGroups()
    {
        Assert.Null(AnalyticsQueryBudget.Violation([QueryFilter.Equals(new string('x', 32_768), "v")], null));
        Assert.NotNull(AnalyticsQueryBudget.Violation([QueryFilter.Equals(new string('x', 32_769), "v")], null));
        Assert.Null(AnalyticsQueryBudget.Violation([], [new string('x', 32_768)]));
        Assert.NotNull(AnalyticsQueryBudget.Violation([QueryFilter.Equals("x", "v")], [new string('x', 32_768)]));
    }

    [Fact]
    public void MappingAndExpressionBoundsAcceptExactlyTheirDeclaredLimits()
    {
        var options = Options();
        var mapping = Enumerable.Range(0, 32).Select(index => new DimensionMappingOptions { From = "k" + index, To = "" }).ToList();
        var declaration = new ComputedDimensionOptions { Name = "mapped", Source = "country", Operation = "map", Mapping = mapping };
        options.ComputedDimensions = [declaration];
        int baseline = Assert.Single(Build(options).Declared).Expression.Length;
        if ((4096 - baseline) % 2 != 0)
        {
            declaration.Source = "os";
            baseline = Assert.Single(Build(options).Declared).Expression.Length;
        }
        int characters = (4096 - baseline) / 2;
        foreach (var entry in mapping)
        {
            int length = Math.Min(characters, 256);
            entry.To = new string('x', length);
            characters -= length;
        }
        Assert.Equal(0, characters);
        Assert.Equal(4096, Assert.Single(Build(options).Declared).Expression.Length);
        mapping[^1].To += "x";
        Assert.Contains("4096 characters", Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
        declaration.Mapping = [new() { From = new string('x', 256), To = new string('y', 256) }];
        declaration.Default = new string('z', 256);
        Assert.Single(Build(options).Declared);
        declaration.Mapping[0].From += "x";
        Assert.Throws<InvalidOperationException>(() => Build(options));
        declaration.Mapping[0].From = "x";
        declaration.Mapping[0].To = null!;
        Assert.Throws<InvalidOperationException>(() => Build(options));
    }

    [Fact]
    public void RepeatedComputedFiltersCannotExceedTheRequestExpansionBudget()
    {
        var options = Options();
        options.ComputedDimensions = [new()
        {
            Name = "mapped", Source = "country", Operation = "map",
            Mapping = [new() { From = new string('x', 256), To = new string('y', 256) }]
        }];
        var dimensions = new DimensionRegistry(options);
        var measures = new MeasureRegistry(options, dimensions);
        var columns = new QueryColumns(dimensions, null, Build(options));
        var result = NamedMetricRequestFactory.Create(new ReadIdentity { ProjectId = "p", TenantId = "t" }, "activity",
            null, null, null, Enumerable.Repeat("mapped:x", 32).ToArray(), new NamedMetricRegistry(options, measures),
            columns, measures, 24, new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc),
            new EventSegmentRegistry(options, columns, measures), null);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("Analytics expression expansion exceeds the 32768-character request budget.", result.Error);
        Assert.Null(result.Request);
    }
}

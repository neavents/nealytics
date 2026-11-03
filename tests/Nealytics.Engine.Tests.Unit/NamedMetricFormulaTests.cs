using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Nealytics.Engine.Features.GetNamedMetric;
using Nealytics.Engine.Features.GetSchema;
using Nealytics.Engine.Features.GetSavedReport;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed class NamedMetricFormulaTests
{
    internal static MetricExpressionOptions Constant(string value) => new() { Operator = "constant", Value = value };
    internal static MetricExpressionOptions Reference(string metric) => new() { Operator = "metric", Metric = metric };
    internal static MetricExpressionOptions Operation(string operation, params MetricExpressionOptions[] arguments) =>
        new() { Operator = operation, Arguments = arguments.ToList() };

    internal static TelemetryEngineOptions Options(MetricExpressionOptions expression)
    {
        var options = SavedReportRegistryTests.Options();
        options.NamedMetrics.Add(new() { Name = "computed", Aggregation = "formula", Expression = expression });
        return options;
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("+12", "12")]
    [InlineData("9007199254740993", "9007199254740993")]
    [InlineData("18446744073709551614", "18446744073709551614")]
    [InlineData("1.00060", "1.0006")]
    [InlineData(".125", "0.125")]
    [InlineData("12.", "12")]
    [InlineData("1e3", "1000")]
    [InlineData("1E-3", "0.001")]
    [InlineData("-1.25e+2", "-125")]
    [InlineData("0.00000000000000000000000000000000005", "0.0000000000000000000000000000000001")]
    [InlineData("-0.00000000000000000000000000000000005", "-0.0000000000000000000000000000000001")]
    [InlineData("0.000000000000000000000000000000000049", "0")]
    [InlineData("-0.000000000000000000000000000000000049", "0")]
    [InlineData("0.99999999999999999999999999999999999", "1")]
    public void ExactInputsAndFractionalRoundingHaveAnExplicitInvariantRepresentation(string input, string expected)
    {
        Assert.Equal(expected, FormulaNumber.Parse(input)!.Value.Format());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData("1.2.3")]
    [InlineData("1..2")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1,2")]
    [InlineData("nan")]
    [InlineData("inf")]
    [InlineData("-inf")]
    [InlineData("١")]
    [InlineData("1e")]
    [InlineData("1e309")]
    [InlineData("1e-309")]
    [InlineData("1e2147483648")]
    [InlineData("1e2e3")]
    [InlineData("e2")]
    public void InvalidAndNonfiniteInputsBecomeNull(string? value)
    {
        Assert.Null(FormulaNumber.Parse(value));
    }

    [Theory]
    [InlineData("add", "9007199254740993", "0.0001", "9007199254740993.0001")]
    [InlineData("subtract", "1.2", "0.2", "1")]
    [InlineData("subtract", "1", "3", "-2")]
    [InlineData("add", "0.5", "0.125", "0.625")]
    [InlineData("subtract", "0.5", "0.125", "0.375")]
    [InlineData("multiply", "0.5", "0.125", "0.0625")]
    [InlineData("divide", "0.5", "0.125", "4")]
    [InlineData("multiply", "1.0006", "100", "100.06")]
    [InlineData("divide", "1", "3", "0.3333333333333333333333333333333333")]
    [InlineData("divide", "1", "6", "0.1666666666666666666666666666666667")]
    [InlineData("divide", "1", "-2", "-0.5")]
    [InlineData("divide", "-1", "-2", "0.5")]
    [InlineData("divide", "0", "7", "0")]
    public void ArithmeticPreservesExactIntermediatesAndRoundsOnlyTheResult(
        string operation, string left, string right, string expected)
    {
        Assert.Equal(expected, FormulaNumber.Apply(operation, FormulaNumber.Parse(left)!.Value,
            FormulaNumber.Parse(right)!.Value)!.Value.Format());
    }

    [Fact]
    public void ZeroDivisionAndBoundedNumeratorsAndDenominatorsCannotAllocateWithoutLimit()
    {
        var one = FormulaNumber.Parse("1")!.Value;
        var zero = FormulaNumber.Parse("0")!.Value;
        Assert.Null(FormulaNumber.Apply("divide", one, zero));
        Assert.Equal("-1", one.Negate().Format());
        Assert.Equal("0", zero.Negate().Format());
        Assert.NotNull(FormulaNumber.Parse(new string('9', 512)));
        Assert.Null(FormulaNumber.Parse(new string('9', 513)));
        var large = FormulaNumber.Parse(new string('9', 512))!.Value;
        var squared = FormulaNumber.Apply("multiply", large, large)!.Value;
        Assert.Null(FormulaNumber.Apply("multiply", squared, large));
        var small = FormulaNumber.Parse("1e-308")!.Value;
        var smaller = FormulaNumber.Apply("multiply", small, small)!.Value;
        smaller = FormulaNumber.Apply("multiply", smaller, smaller)!.Value;
        Assert.Null(FormulaNumber.Apply("multiply", smaller, small));
        Assert.Equal("1" + new string('0', 308), FormulaNumber.Parse("1e308")!.Value.Format());
        Assert.Equal("Unknown compiled formula operation.",
            Assert.Throws<InvalidOperationException>(() => FormulaNumber.Apply("unknown", one, one)).Message);
    }

    [Fact]
    public void ArithmeticDoesNotDependOnTheCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("-1.25", FormulaNumber.Parse("-1.250")!.Value.Format());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void BitLengthBoundsAcceptTheirEndpointsForBothSignsAndTheDenominator()
    {
        var one = FormulaNumber.Parse("1")!.Value;
        BigInteger boundary = BigInteger.One << 4095;
        foreach (int sign in new[] { -1, 1 })
        {
            var value = new FormulaNumber(sign * boundary, BigInteger.One);
            Assert.Equal(value, FormulaNumber.Apply("multiply", value, one));
            Assert.Null(FormulaNumber.Apply("multiply", new FormulaNumber(sign * (boundary << 1), BigInteger.One), one));
        }
        var fraction = new FormulaNumber(BigInteger.One, boundary);
        Assert.Equal(fraction, FormulaNumber.Apply("multiply", fraction, one));
        Assert.Null(FormulaNumber.Apply("multiply", new FormulaNumber(BigInteger.One, boundary << 1), one));
    }

    [Fact]
    public void ForwardReferencesCompileAndPublicMetadataIsDetachedAndImmutable()
    {
        var expression = Operation(" ADD ", Reference(" later "), Constant(" 0.0001 "));
        var options = Options(expression);
        options.NamedMetrics.Add(new() { Name = "later", Aggregation = "formula", Expression = Reference("activity") });
        var registry = NamedMetricRegistryTests.Build(options);
        var metric = registry.Find("computed")!;
        Assert.Equal("formula", metric.Aggregation);
        Assert.Equal("add", metric.Expression!.Operator);
        Assert.Null(metric.Expression.Value);
        Assert.Null(metric.Expression.Metric);
        Assert.Equal("later", metric.Expression.Arguments[0].Metric);
        Assert.Equal("0.0001", metric.Expression.Arguments[1].Value);
        Assert.Equal("activity", Assert.Single(metric.Dependencies).Name);
        Assert.Same(registry.Find("later"), metric.CompiledExpression!.Arguments[0].Metric);
        Assert.Same(registry.Find("activity"), registry.Find("later")!.CompiledExpression!.Metric);
        Assert.Equal("9007199254740993.0001", MetricProjection.Evaluate(metric,
            new Dictionary<string, string?> { ["activity"] = "9007199254740993" }));
        expression.Operator = "divide";
        expression.Arguments.Clear();
        options.NamedMetrics.Clear();
        Assert.Equal("add", metric.Expression.Operator);
        Assert.Equal(2, metric.Expression.Arguments.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<MetricExpression>)metric.Expression.Arguments).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<NamedMetric>)metric.Dependencies).Clear());
    }

    [Theory]
    [InlineData(0, "requires an expression")]
    [InlineData(1, "cannot declare a measure")]
    [InlineData(2, "cannot declare a measure")]
    [InlineData(3, "requires formula aggregation")]
    [InlineData(4, "operator is not supported")]
    [InlineData(5, "operator is not supported")]
    [InlineData(6, "argument count")]
    [InlineData(7, "argument count")]
    [InlineData(8, "nonnull nodes")]
    [InlineData(9, "unsupported by its operator")]
    [InlineData(10, "unsupported by its operator")]
    [InlineData(11, "reference is not declared")]
    [InlineData(12, "reference is not declared")]
    public void InvalidFormulaDeclarationsFailBeforeActivation(int invalidCase, string reason)
    {
        var expression = Operation("add", Reference("activity"), Constant("1"));
        var options = Options(expression);
        var metric = options.NamedMetrics[1];
        switch (invalidCase)
        {
            case 0: metric.Expression = null; break;
            case 1: metric.Measure = "amount"; break;
            case 2: metric.EventType = "action"; break;
            case 3: metric.Aggregation = "count"; break;
            case 4: expression.Operator = null!; break;
            case 5: expression.Operator = "sql"; break;
            case 6: expression.Arguments = null!; break;
            case 7: expression.Arguments.RemoveAt(0); break;
            case 8: expression.Arguments[0] = null!; break;
            case 9: expression.Value = "1"; break;
            case 10: expression.Metric = "activity"; break;
            case 11: expression.Arguments[0].Metric = "missing"; break;
            case 12: expression.Arguments[0].Metric = null!; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options));
        Assert.StartsWith("TelemetryEngine:NamedMetrics[1]: ", error.Message);
        Assert.Contains(reason, error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nan")]
    [InlineData("inf")]
    [InlineData("1e2")]
    [InlineData("+1")]
    [InlineData(".1")]
    [InlineData("1.")]
    [InlineData("1,2")]
    [InlineData("1;SELECT")]
    public void ConstantsAreStrictDecimalDeclarations(string? value)
    {
        var options = Options(Constant(value!));
        Assert.Contains("decimal value with at most 38 digits",
            Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
        options.NamedMetrics[1].Expression = Constant("1");
        options.NamedMetrics[1].Expression!.Metric = "activity";
        Assert.Contains("decimal value", Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
    }

    [Fact]
    public void ConstantDigitBoundIncludesThirtyEightDigits()
    {
        var options = Options(Constant(new string('9', 38)));
        Assert.Equal(new string('9', 38), MetricProjection.Evaluate(NamedMetricRegistryTests.Build(options).Find("computed")!,
            new Dictionary<string, string?>()));
        options.NamedMetrics[1].Expression!.Value += "9";
        Assert.Contains("38 digits", Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
    }

    [Fact]
    public void ConstantPrecisionCountsZeroDigitsAndExcludesSignsAndDecimalPoints()
    {
        foreach (string value in new[] { "-" + new string('9', 38), new string('9', 19) + "." + new string('9', 19), new string('0', 38) })
        {
            Assert.NotNull(NamedMetricRegistryTests.Build(Options(Constant(value))).Find("computed"));
        }
        Assert.Contains("38 digits", Assert.Throws<InvalidOperationException>(() =>
            NamedMetricRegistryTests.Build(Options(Constant(new string('0', 39))))).Message);
    }

    [Fact]
    public void NegationPreservesDimensionlessDisplayAnnotationsFromReferencedFormulas()
    {
        var options = Options(Operation("negate", Reference("labeled")));
        options.NamedMetrics.Add(new() { Name = "labeled", Aggregation = "formula", Unit = "percent", Expression = Constant("1") });
        var metric = NamedMetricRegistryTests.Build(options).Find("computed")!;
        Assert.Equal("percent", metric.Unit);
        Assert.Equal("-1", MetricProjection.Evaluate(metric, new Dictionary<string, string?>()));
    }

    [Fact]
    public void CyclesAreRejectedIncludingDirectSelfReferences()
    {
        var options = Options(Reference("computed"));
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
        options.NamedMetrics[1].Expression = Reference("later");
        options.NamedMetrics.Add(new() { Name = "later", Aggregation = "formula", Expression = Reference("computed") });
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
    }

    private static MetricExpressionOptions Tree(int depth) => depth == 1 ? Constant("1")
        : Operation("add", Tree(depth - 1), Tree(depth - 1));

    [Fact]
    public void DepthAndExpandedNodeBoundsIncludeTheirExactLimits()
    {
        var options = Options(Operation("negate", Tree(6)));
        Assert.Equal("-32", MetricProjection.Evaluate(NamedMetricRegistryTests.Build(options).Find("computed")!,
            new Dictionary<string, string?>()));
        options.NamedMetrics[1].Expression = Operation("add", Tree(6), Constant("1"));
        Assert.Contains("64 expanded nodes", Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
        var expression = Constant("1");
        for (int index = 0; index < 7; index++)
        {
            expression = Operation("negate", expression);
        }
        options.NamedMetrics[1].Expression = expression;
        Assert.Equal("-1", MetricProjection.Evaluate(NamedMetricRegistryTests.Build(options).Find("computed")!,
            new Dictionary<string, string?>()));
        options.NamedMetrics[1].Expression = Operation("negate", expression);
        Assert.Contains("depth at most eight", Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void ReferenceExpansionAlsoEnforcesTheDepthBound(int length, bool valid)
    {
        var options = new TelemetryEngineOptions();
        for (int index = 0; index < length; index++)
        {
            options.NamedMetrics.Add(new()
            {
                Name = "f" + index, Aggregation = "formula",
                Expression = index == length - 1 ? Constant("1") : Reference("f" + (index + 1))
            });
        }
        if (valid)
        {
            Assert.Equal("1", MetricProjection.Evaluate(NamedMetricRegistryTests.Build(options).Find("f0")!,
                new Dictionary<string, string?>()));
        }
        else
        {
            Assert.Contains("dependency depth exceeds eight", Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
        }
        options.NamedMetrics.Reverse();
        if (valid)
        {
            Assert.NotNull(NamedMetricRegistryTests.Build(options).Find("f0"));
        }
        else
        {
            Assert.Contains("expanded depth or node count", Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
        }
    }

    [Fact]
    public void ReferenceExpansionIncludesExactlySixtyFourNodesAndCountsArgumentDepth()
    {
        var options = Options(Reference("large"));
        options.NamedMetrics.Add(new() { Name = "large", Aggregation = "formula", Expression = Tree(6) });
        Assert.Equal("32", MetricProjection.Evaluate(NamedMetricRegistryTests.Build(options).Find("computed")!,
            new Dictionary<string, string?>()));
        var expression = Constant("1");
        for (int index = 0; index < 7; index++)
        {
            expression = Operation("negate", expression);
        }
        options.NamedMetrics[2].Expression = expression;
        Assert.Contains("expanded depth or node count", Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
    }

    [Fact]
    public void IndependentFormulasDoNotAccumulateDependencyDepth()
    {
        var options = new TelemetryEngineOptions();
        for (int index = 0; index < 128; index++)
        {
            options.NamedMetrics.Add(new() { Name = "f" + index, Aggregation = "formula", Expression = Constant("1") });
        }
        Assert.Equal(128, NamedMetricRegistryTests.Build(options).Declared.Count);
    }

    [Fact]
    public void DimensionlessAnnotationsCanBeOverriddenWithoutChangingDimensionalInference()
    {
        var options = Options(Reference("labeled"));
        options.NamedMetrics[1].Unit = "ratio";
        options.NamedMetrics.Add(new() { Name = "labeled", Aggregation = "formula", Unit = "percent", Expression = Constant("1") });
        var compiled = NamedMetricRegistryTests.Build(options).Find("computed")!;
        Assert.Equal("ratio", compiled.Unit);
        Assert.Null(compiled.CompiledExpression!.UnitKey);
        options.NamedMetrics[0].Measure = "amount";
        options.NamedMetrics[1].Expression = Operation("add", Reference("activity"), Constant("1"));
        Assert.NotNull(NamedMetricRegistryTests.Build(options).Find("computed"));
    }

    [Fact]
    public void RepeatedReferencesCountExpandedNodesEvenWhenProjectionComponentsAreShared()
    {
        var options = Options(Operation("add", Reference("large"), Reference("large")));
        options.NamedMetrics.Add(new() { Name = "large", Aggregation = "formula", Expression = Tree(4) });
        Assert.NotNull(NamedMetricRegistryTests.Build(options).Find("computed"));
        options.NamedMetrics[2].Expression = Tree(5);
        Assert.Contains("expanded", Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
    }

    [Theory]
    [InlineData("add", "records", "records", true, "records")]
    [InlineData("subtract", "records", "records", true, "records")]
    [InlineData("add", "records", "seconds", false, null)]
    [InlineData("subtract", "records", null, false, null)]
    [InlineData("multiply", "records", "seconds", false, null)]
    [InlineData("multiply", "records", null, true, "records")]
    [InlineData("multiply", null, "seconds", true, "seconds")]
    [InlineData("divide", "records", "records", true, null)]
    [InlineData("divide", "records", null, true, "records")]
    [InlineData("divide", null, "seconds", false, null)]
    [InlineData("divide", "records", "seconds", false, null)]
    public void UnitInferenceRejectsUnsupportedDimensionalArithmetic(
        string operation, string? leftUnit, string? rightUnit, bool valid, string? expectedUnit)
    {
        var options = Options(Operation(operation, Reference("activity"), Reference("other")));
        options.NamedMetrics[0].Unit = leftUnit!;
        options.NamedMetrics.Add(new() { Name = "other", Unit = rightUnit! });
        if (valid)
        {
            Assert.Equal(expectedUnit, NamedMetricRegistryTests.Build(options).Find("computed")!.Unit);
        }
        else
        {
            Assert.Contains("incompatible dimensional units",
                Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
        }
    }

    [Fact]
    public void DimensionalUnitsCannotBeRelabeledAndUnannotatedMeasuresAreNotAssumedCompatible()
    {
        var options = Options(Operation("negate", Reference("activity")));
        options.NamedMetrics[0].Unit = "records";
        options.NamedMetrics[1].Unit = "seconds";
        Assert.Contains("inferred dimensional unit", Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
        options.NamedMetrics[1].Unit = "records";
        Assert.Equal("records", NamedMetricRegistryTests.Build(options).Find("computed")!.Unit);
        options.NamedMetrics[0].Measure = "amount";
        options.NamedMetrics[0].Aggregation = "sum";
        options.NamedMetrics[0].Unit = "";
        options.NamedMetrics[1].Unit = "";
        Assert.Equal("unit_key", NamedMetricRegistryTests.Build(options).Find("computed")!.UnitDimension);
        options.Measures[0].UnitDimension = "";
        options.Measures.Add(new() { Name = "other_amount", Type = "Int64" });
        options.NamedMetrics.Add(new() { Name = "other", Aggregation = "sum", Measure = "other_amount" });
        options.NamedMetrics[1].Expression = Operation("add", Reference("activity"), Reference("other"));
        Assert.Contains("incompatible dimensional units", Assert.Throws<InvalidOperationException>(() => NamedMetricRegistryTests.Build(options)).Message);
    }

    [Fact]
    public void NullPropagationZeroDivisionAndConstantOnlyPlansAreDefined()
    {
        var options = Options(Operation("add", Reference("activity"), Constant("1")));
        var metric = NamedMetricRegistryTests.Build(options).Find("computed")!;
        Assert.Null(MetricProjection.Evaluate(metric, new Dictionary<string, string?> { ["activity"] = null }));
        options.NamedMetrics[1].Expression = Operation("add", Constant("1"), Reference("activity"));
        metric = NamedMetricRegistryTests.Build(options).Find("computed")!;
        Assert.Null(MetricProjection.Evaluate(metric, new Dictionary<string, string?> { ["activity"] = "inf" }));
        options.NamedMetrics[1].Expression = Operation("divide", Constant("1"), Constant("0"));
        metric = NamedMetricRegistryTests.Build(options).Find("computed")!;
        Assert.Null(MetricProjection.Evaluate(metric, new Dictionary<string, string?>()));
        options.NamedMetrics[1].Expression = Constant("1");
        metric = NamedMetricRegistryTests.Build(options).Find("computed")!;
        var query = GetNamedMetricQuery.BuildQuery(new NamedMetricRequest(metric, new QueryScope
        {
            ProjectId = "p", TenantId = "t", Filters = []
        }));
        Assert.StartsWith("SELECT toString(count()) FROM nealytics_core.global_events", query.Sql);
        Assert.Empty(new MetricProjection([metric]).Components);
    }

    [Fact]
    public void PrimitiveDependenciesAreDeduplicatedAndBoundRatherThanInterpolated()
    {
        var options = Options(Operation("divide", Reference("activity"), Reference("activity")));
        options.NamedMetrics[0].EventType = "action' OR 1=1";
        options.SavedReports[0].Fields.Add(new() { Name = "ratio", Metric = "computed" });
        var report = SavedReportRegistryTests.Build(options).Find("overview")!;
        var query = GetSavedReportQuery.BuildQuery(new SavedReportRequest(report, new QueryScope
        {
            ProjectId = "p", TenantId = "t", Filters = []
        }));
        Assert.StartsWith("SELECT toString(countIf(event_type = {reportEvent0:String})) FROM", query.Sql);
        Assert.Equal("action' OR 1=1", query.Parameters.Single(pair => pair.Key == "reportEvent0").Value);
        Assert.DoesNotContain("action' OR", query.Sql);
        Assert.Single(new MetricProjection(report.Fields.Select(field => field.Definition)).Components);
    }

    [Fact]
    public void EveryFormulaComponentRetainsItsUnitRequirementBeforeStorage()
    {
        var options = Options(Operation("divide", Reference("total"), Reference("total")));
        options.NamedMetrics.Add(new() { Name = "total", Aggregation = "sum", Measure = "amount" });
        var dimensions = new DimensionRegistry(options);
        var measures = new MeasureRegistry(options, dimensions);
        var metrics = new NamedMetricRegistry(options, measures);
        var columns = new QueryColumns(dimensions);
        var segments = new EventSegmentRegistry(options, columns, measures);
        var identity = new ReadIdentity { ProjectId = "p", TenantId = "t" };
        var now = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        var invalid = NamedMetricRequestFactory.Create(identity, "computed", null, null, null, [],
            metrics, columns, measures, 24, now, segments, null);
        Assert.Equal(400, invalid.StatusCode);
        Assert.Contains("filter=unit_key:<value>", invalid.Error);
        var valid = NamedMetricRequestFactory.Create(identity, "computed", null, null, null, ["unit_key:u"],
            metrics, columns, measures, 24, now, segments, null);
        Assert.Equal(200, valid.StatusCode);
        Assert.Null(valid.Request!.Scope.EventType);
    }

    [Fact]
    public void NestedDimensionalFormulasAndMeasureCountsPreserveTheirOwnUnits()
    {
        var options = Options(Reference("nested"));
        options.NamedMetrics[0].Unit = "records";
        options.NamedMetrics.Add(new() { Name = "nested", Aggregation = "formula", Expression = Reference("activity") });
        var registry = NamedMetricRegistryTests.Build(options);
        Assert.Equal("records", registry.Find("computed")!.Unit);
        Assert.Equal("7", MetricProjection.Evaluate(registry.Find("activity")!,
            new Dictionary<string, string?> { ["activity"] = "7" }));
        options.NamedMetrics[0].Unit = "";
        options.NamedMetrics[0].Measure = "amount";
        Assert.Null(NamedMetricRegistryTests.Build(options).Find("computed")!.Unit);
    }

    [Theory]
    [InlineData("count", "", "", "records", "Annotation", "records", null)]
    [InlineData("sum", "amount", "unit_key", "", "Dimension", "unit_key", null)]
    [InlineData("sum", "amount", "", "", "Measure", "amount", null)]
    [InlineData("sum", "amount", "unit_key", "units", "Annotation", "units", "unit_key")]
    public void DimensionalIdentityUsesTypedComponentsWithoutAmbiguousStringTags(
        string aggregation, string measure, string dimension, string unit, string kind, string name, string? keyDimension)
    {
        var options = Options(Operation("multiply", Reference("activity"), Constant("1")));
        options.NamedMetrics[0].Aggregation = aggregation;
        options.NamedMetrics[0].Measure = measure;
        options.NamedMetrics[0].Unit = unit;
        options.Measures[0].UnitDimension = dimension;
        var metric = NamedMetricRegistryTests.Build(options).Find("computed")!;
        var key = metric.CompiledExpression!.UnitKey!.Value;
        Assert.Equal(kind, key.Kind.ToString());
        Assert.Equal(name, key.Name);
        Assert.Equal(keyDimension, key.Dimension);
        Assert.Equal(string.IsNullOrEmpty(unit) ? null : unit, metric.Unit);
        Assert.Equal("2", MetricProjection.Evaluate(metric, new Dictionary<string, string?> { ["activity"] = "2" }));
    }

    [Fact]
    public void DependenciesWithDifferentUnitDimensionsAreAllValidatedAfterDimensionalCancellation()
    {
        var options = Options(Operation("multiply", Reference("total"),
            Operation("divide", Reference("other"), Reference("other"))));
        options.Dimensions.Add(new() { Name = "other_unit" });
        options.Measures.Add(new() { Name = "other_amount", Type = "Int64", UnitDimension = "other_unit" });
        options.NamedMetrics.Add(new() { Name = "total", Aggregation = "sum", Measure = "amount" });
        options.NamedMetrics.Add(new() { Name = "other", Aggregation = "sum", Measure = "other_amount" });
        var dimensions = new DimensionRegistry(options);
        var measures = new MeasureRegistry(options, dimensions);
        var metric = NamedMetricRegistryTests.Build(options).Find("computed")!;
        Assert.Null(metric.UnitDimension);
        Assert.Contains("other_unit", MetricProjection.UnitViolation(metric, measures,
            [QueryFilter.Equals("unit_key", "u")]));
        Assert.Null(MetricProjection.UnitViolation(metric, measures,
            [QueryFilter.Equals("unit_key", "u"), QueryFilter.Equals("other_unit", "v")]));
    }

    private static MetricExpressionOptions References(IReadOnlyList<string> names) => names.Count == 1
        ? Reference(names[0]) : Operation("add", References(names.Take(names.Count / 2).ToArray()),
            References(names.Skip(names.Count / 2).ToArray()));

    [Fact]
    public void SavedReportProjectionHasAnIndependentAggregateComponentBound()
    {
        var options = new TelemetryEngineOptions { MaxNamedMetrics = 256 };
        for (int index = 0; index < 129; index++)
        {
            options.NamedMetrics.Add(new() { Name = "m" + index });
        }
        for (int group = 0; group < 4; group++)
        {
            options.NamedMetrics.Add(new()
            {
                Name = "f" + group, Aggregation = "formula",
                Expression = References(Enumerable.Range(group * 32, 32).Select(index => "m" + index).ToArray())
            });
        }
        options.SavedReports = [new()
        {
            Name = "overview", Fields = Enumerable.Range(0, 4)
                .Select(index => new SavedReportFieldOptions { Name = "v" + index, Metric = "f" + index }).ToList()
        }];
        var report = SavedReportRegistryTests.Build(options).Find("overview")!;
        Assert.Equal(128, new MetricProjection(report.Fields.Select(field => field.Definition)).Components.Count);
        options.SavedReports[0].Fields.Add(new() { Name = "extra", Metric = "m128" });
        Assert.Equal("TelemetryEngine:SavedReports[0]: report aggregate projection exceeds 128 components.",
            Assert.Throws<InvalidOperationException>(() => SavedReportRegistryTests.Build(options)).Message);
    }

    [Fact]
    public void SchemaSerializationExposesExpressionsWithoutCompiledReferences()
    {
        var options = Options(Reference("activity"));
        var schema = new SchemaResponse { NamedMetrics = NamedMetricRegistryTests.Build(options).Declared };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(schema, TelemetryAotContext.Default.SchemaResponse));
        var computed = document.RootElement.GetProperty("namedMetrics")[1];
        Assert.Equal("metric", computed.GetProperty("expression").GetProperty("operator").GetString());
        Assert.Equal("activity", computed.GetProperty("expression").GetProperty("metric").GetString());
        Assert.False(computed.TryGetProperty("compiledExpression", out _));
        Assert.False(computed.TryGetProperty("dependencies", out _));
        var declaration = new MetricExpressionOptions();
        Assert.Equal("", declaration.Operator);
        Assert.Equal("", declaration.Value);
        Assert.Equal("", declaration.Metric);
        Assert.Empty(declaration.Arguments);
        Assert.Null(new NamedMetricOptions().Expression);
    }
}

using System.Text;
using Nealytics.Engine.Features.GetNamedMetric;
using Nealytics.Engine.Features.GetSavedReport;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Features.GetAnalyticsPlan;

namespace Nealytics.Engine.Tests.Unit;

public sealed class MetricAccuracyTests
{
    [Fact]
    public void DefaultMetadataKeepsRawExactExecutionAndEmptyAdministrativeCollections()
    {
        var response = new AnalyticsPlanResponse();
        Assert.Equal("", response.Name);
        Assert.Equal(0, response.Version);
        Assert.Equal("raw", response.Source);
        Assert.Equal("exact", response.Accuracy);
        Assert.Null(response.SelectedRollup);
        Assert.Empty(response.Candidates);
        Assert.Empty(response.Slices);
        Assert.Equal("exact", new NamedMetricResponse().Accuracy);
        Assert.Equal("exact", new SavedReportResponse().Accuracy);
        Assert.False(new TelemetryEngineOptions().EnableAnalyticsRollupPlanning);
        Assert.False(new TelemetryEngineOptions().AnalyticsRollupSourceIsAppendOnly);
    }

    private static NamedMetricRegistry Build(TelemetryEngineOptions options) =>
        new(options, new MeasureRegistry(options, new DimensionRegistry(options)));

    [Theory]
    [InlineData("p50", "0.5")]
    [InlineData("p75", "0.75")]
    [InlineData("p90", "0.9")]
    [InlineData("p95", "0.95")]
    [InlineData("p99", "0.99")]
    public void QuantilesHaveExplicitExactAndApproximateAlgorithmsInScalarAndConditionalSql(string aggregation, string level)
    {
        var options = AnalyticsPlanningTests.Options();
        foreach (string accuracy in new[] { "exact", "approximate" })
        {
            options.NamedMetrics = [new() { Name = "q", Measure = "amount", Aggregation = aggregation, Accuracy = " " + accuracy.ToUpperInvariant() + " ", EventType = "action" }];
            var metric = Assert.Single(Build(options).Declared);
            Assert.Equal(accuracy, metric.Accuracy);
            var sql = new StringBuilder();
            MetricAggregateSql.Append(sql, metric);
            string function = accuracy == "exact" ? "quantileExactOrNull" : "quantileOrNull";
            Assert.Equal($"toString({function}({level})(amount))", sql.ToString());
            sql.Clear();
            MetricAggregateSql.Append(sql, metric, 2);
            Assert.Equal($"toString({function}If({level})(amount, event_type = {{reportEvent2:String}}))", sql.ToString());
        }
    }

    [Theory]
    [InlineData("count", "approximate")]
    [InlineData("sum", "approximate")]
    [InlineData("p95", "unknown")]
    [InlineData("p95", "")]
    [InlineData("p95", null)]
    public void UnsupportedAccuracyDeclarationsFailBeforeActivation(string aggregation, string? accuracy)
    {
        var options = AnalyticsPlanningTests.Options();
        options.NamedMetrics = [new() { Name = "q", Measure = "amount", Aggregation = aggregation, Accuracy = accuracy! }];
        Assert.Contains("accuracy must be exact, or approximate for quantiles and formulas", Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
    }

    [Fact]
    public void ExactFormulasCannotConcealApproximateDependencies()
    {
        var options = AnalyticsPlanningTests.Options();
        options.NamedMetrics =
        [
            new() { Name = "q", Measure = "amount", Aggregation = "p95", Accuracy = "approximate" },
            new() { Name = "formula", Aggregation = "formula", Expression = new() { Operator = "metric", Metric = "q" } }
        ];
        Assert.Contains("an exact formula cannot contain an approximate dependency", Assert.Throws<InvalidOperationException>(() => Build(options)).Message);
        options.NamedMetrics[1].Accuracy = "approximate";
        var registry = Build(options);
        Assert.Equal("approximate", registry.Find("formula")!.Accuracy);
        Assert.Equal("approximate", AnalyticsExecution.Accuracy(registry.Declared));
        options.NamedMetrics[0].Accuracy = "exact";
        options.NamedMetrics[1].Accuracy = "exact";
        Assert.Equal("exact", AnalyticsExecution.Accuracy(Build(options).Declared));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", null)]
    [InlineData("auto", "exact", null)]
    [InlineData("raw", "approximate", null)]
    [InlineData("rollup", "exact", "Execution must be auto or raw.")]
    [InlineData("AUTO", "exact", "Execution must be auto or raw.")]
    [InlineData("raw", "invalid", "Accuracy must be exact or approximate.")]
    public void RequestExecutionAndAccuracyOptionsUseAnExplicitFiniteContract(string? execution, string? accuracy, string? error)
    {
        Assert.Equal(error, AnalyticsExecution.Violation(execution, accuracy, []));
    }

    [Fact]
    public void BothRequestFactoriesRequireApproximateOptInForEveryField()
    {
        var options = AnalyticsPlanningTests.Options();
        options.NamedMetrics.Add(new() { Name = "q", Measure = "amount", Aggregation = "p95", Accuracy = "approximate" });
        options.SavedReports = [new() { Name = "r", Fields = [new() { Name = "events", Metric = "events" }, new() { Name = "q", Metric = "q" }] }];
        var dimensions = new DimensionRegistry(options);
        var measures = new MeasureRegistry(options, dimensions);
        var columns = new QueryColumns(dimensions);
        var metrics = Build(options);
        var segments = new EventSegmentRegistry(options, columns, measures);
        var reports = new SavedReportRegistry(options, metrics, segments);
        var identity = new ReadIdentity { ProjectId = "p", TenantId = "t" };
        var now = new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        NamedMetricRequestResult Metric(string? execution, string? accuracy) => NamedMetricRequestFactory.Create(identity,
            "q", null, null, null, [], metrics, columns, measures, 24, now, segments, null, execution: execution, accuracy: accuracy);
        SavedReportRequestResult Report(string? execution, string? accuracy) => SavedReportRequestFactory.Create(identity,
            "r", null, null, null, [], reports, metrics, segments, columns, measures, 24, now, execution: execution, accuracy: accuracy);
        const string required = "This definition contains approximate metrics; explicitly request accuracy=approximate.";
        Assert.Equal(required, Metric(null, null).Error);
        Assert.Equal(required, Report(null, null).Error);
        Assert.Equal(400, Metric(null, "exact").StatusCode);
        Assert.Equal(400, Report(null, "exact").StatusCode);
        Assert.Equal("raw", Metric("raw", "approximate").Request!.Execution);
        Assert.Equal("raw", Report("raw", "approximate").Request!.Execution);
        Assert.Equal("auto", Metric("", "approximate").Request!.Execution);
        Assert.Equal("auto", Report(null, "approximate").Request!.Execution);
        Assert.Equal("Execution must be auto or raw.", Metric("invalid", "approximate").Error);
        Assert.Equal("Accuracy must be exact or approximate.", Report("auto", "invalid").Error);
        Assert.Null(SavedReportRequestFactory.UnsupportedParameter(["execution", "accuracy"]));
    }
}

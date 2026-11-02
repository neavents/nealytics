using System.Globalization;
using System.Text;
using System.Text.Json;
using Nealytics.Engine.Features.GetSavedReport;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed class SavedReportRequestTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private static SavedReportRequestResult Create(
        ReadIdentity? identity = null, string name = "overview", string? from = null, string? to = null,
        string? traffic = null, string[]? filters = null, TelemetryEngineOptions? options = null)
    {
        options ??= SavedReportRegistryTests.Options();
        var dimensions = new DimensionRegistry(options);
        var columns = new QueryColumns(dimensions);
        var measures = new MeasureRegistry(options, dimensions);
        var metrics = new NamedMetricRegistry(options, measures);
        var segments = new EventSegmentRegistry(options, columns, measures);
        return SavedReportRequestFactory.Create(identity ?? new() { ProjectId = "project", TenantId = "tenant" },
            name, from, to, traffic, filters ?? [], new SavedReportRegistry(options, metrics, segments),
            metrics, segments, columns, measures, 7, Now.AddTicks(12_345));
    }

    [Fact]
    public void DefaultsPreserveCommonScopeAndNeverApplyOneFieldsEventTypeToTheWholeReport()
    {
        var options = SavedReportRegistryTests.Options();
        options.NamedMetrics[0].EventType = "action";
        var result = Create(options: options);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Error);
        var request = result.Request!;
        Assert.Equal("overview", request.Definition.Name);
        Assert.Equal("action", request.Definition.Fields[0].Definition.EventType);
        Assert.Equal("project", request.Scope.ProjectId);
        Assert.Equal("tenant", request.Scope.TenantId);
        Assert.Null(request.Scope.TenantSet);
        Assert.Null(request.Scope.EventType);
        Assert.Equal("normal", request.Scope.TrafficClass);
        Assert.Empty(request.Scope.Filters);
        Assert.Equal(Now.AddMilliseconds(1).AddHours(-7), request.Scope.From);
        Assert.Equal(Now.AddMilliseconds(1), request.Scope.To);
        Assert.Equal(DateTimeKind.Utc, request.Scope.To.Kind);
    }

    [Theory]
    [MemberData(nameof(NamedMetricRequestTests.InvalidIdentities), MemberType = typeof(NamedMetricRequestTests))]
    public void InvalidIdentityHasPrecedenceOverUnknownReports(ReadIdentity identity, int status, string? error)
    {
        var result = Create(identity, name: "missing");
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(error, result.Error);
        Assert.Null(result.Request);
    }

    [Fact]
    public void ExactIdentityBoundsAndTenantSetNarrowingArePreserved()
    {
        var identity = new ReadIdentity
        {
            ProjectId = new string('p', 256), TenantId = new string('t', 256),
            TenantSet = new() { Attribute = "cohort", Value = "group" }
        };
        var scope = Create(identity).Request!.Scope;
        Assert.Equal(identity.ProjectId, scope.ProjectId);
        Assert.Equal(identity.TenantId, scope.TenantId);
        Assert.Equal(identity.TenantSet, scope.TenantSet);
        Assert.Equal("", Create(identity with { TenantId = null }).Request!.Scope.TenantId);
        Assert.Equal(403, Create(identity with { ProjectId = null! }).StatusCode);
        Assert.Equal(403, Create(identity with { TenantSet = null, TenantId = null }).StatusCode);
    }

    [Theory]
    [InlineData("invalid", null, null, null)]
    [InlineData(null, "invalid", null, null)]
    [InlineData(null, null, "invalid", null)]
    [InlineData(null, null, null, "missing:value")]
    [InlineData("2300-01-01", null, null, null)]
    [InlineData("2026-11-03T12:00:00Z", "2026-11-03T12:00:00Z", null, null)]
    public void SharedValidationFailuresPropagateBeforeExecution(string? from, string? to, string? traffic, string? filter)
    {
        var result = Create(from: from, to: to, traffic: traffic, filters: filter is null ? [] : [filter]);
        Assert.Equal(400, result.StatusCode);
        Assert.NotEmpty(result.Error!);
        Assert.Null(result.Request);
    }

    [Fact]
    public void UnknownReportHasItsOwnDiagnosticBeforeParameterParsing()
    {
        var result = Create(name: "missing", from: "invalid");
        Assert.Equal(404, result.StatusCode);
        Assert.Equal("Unknown saved report.", result.Error);
        Assert.Null(result.Request);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnitSafetyAppliesToEveryFieldBeforeStorage(int sumIndex)
    {
        var options = SavedReportRegistryTests.Options();
        options.NamedMetrics.Add(new() { Name = "total", Aggregation = "sum", Measure = "amount" });
        options.SavedReports[0].Fields = Enumerable.Range(0, 3).Select(index => new SavedReportFieldOptions
        {
            Name = "f" + index, Metric = index == sumIndex ? "total" : "activity"
        }).ToList();
        var invalid = Create(options: options);
        Assert.Null(invalid.Request);
        Assert.Equal(400, invalid.StatusCode);
        Assert.Contains("filter=unit_key:<value>", invalid.Error);
        if (sumIndex > 0)
        {
            Assert.StartsWith($"Field 'f{sumIndex}': ", invalid.Error);
        }
        Assert.Equal(200, Create(options: options, filters: ["unit_key:u"]).StatusCode);
        options.EventSegments = [new() { Name = "selected", Filters = ["unit_key:u", "amount>8"] }];
        options.SavedReports[0].Segment = "selected";
        var selected = Create(options: options, filters: ["country:x"]);
        Assert.Equal(200, selected.StatusCode);
        Assert.Equal(new[] { "u", "8", "x" }, selected.Request!.Scope.Filters.Select(filter => filter.Value));
        Assert.Equal("selected", selected.Request.Definition.Segment);
    }

    [Fact]
    public void MeasureCountsDoNotRequireAUnitAndTrafficAndFiltersKeepTheirMeaning()
    {
        var options = SavedReportRegistryTests.Options();
        options.NamedMetrics.Add(new() { Name = "counted", Aggregation = "count", Measure = "amount" });
        options.SavedReports[0].Fields.Add(new() { Name = "present", Metric = "counted" });
        var result = Create(options: options, traffic: "all", filters: ["country:x' OR 1=1", "amount>=2"]);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Request!.Scope.TrafficClass);
        Assert.Equal("x' OR 1=1", result.Request.Scope.Filters[0].Value);
        Assert.Equal(FilterComparison.GreaterOrEqual, result.Request.Scope.Filters[1].Comparison);
        Assert.Equal("bot", Create(traffic: "bot").Request!.Scope.TrafficClass);
    }

    [Theory]
    [InlineData("segment")]
    [InlineData("measure")]
    [InlineData("aggregation")]
    [InlineData("groupBy")]
    [InlineData("FROM")]
    [InlineData("")]
    public void OnlyTheDocumentedReportParametersAreAccepted(string parameter)
    {
        Assert.Equal("Only from, to, traffic, filter and tenant are supported report parameters.",
            SavedReportRequestFactory.UnsupportedParameter(["from", parameter, "to"]));
    }

    [Fact]
    public void AllSupportedParametersAndAnEmptyQueryAreAccepted()
    {
        Assert.Null(SavedReportRequestFactory.UnsupportedParameter(["from", "to", "traffic", "filter", "tenant"]));
        Assert.Null(SavedReportRequestFactory.UnsupportedParameter([]));
    }

    [Theory]
    [InlineData("count", false, "count()", "countIf(event_type = {reportEvent7:String})")]
    [InlineData("count", true, "count(amount)", "countIf(isNotNull(amount) AND event_type = {reportEvent7:String})")]
    [InlineData("sum", true, "coalesce(sum(toInt256(amount)), 0)", "coalesce(sumIf(toInt256(amount), event_type = {reportEvent7:String}), 0)")]
    [InlineData("avg", true, "avgOrNull(amount)", "avgOrNullIf(amount, event_type = {reportEvent7:String})")]
    [InlineData("min", true, "minOrNull(amount)", "minOrNullIf(amount, event_type = {reportEvent7:String})")]
    [InlineData("max", true, "maxOrNull(amount)", "maxOrNullIf(amount, event_type = {reportEvent7:String})")]
    public void ScalarAggregateSqlKeepsExactSemanticsWithAndWithoutConditions(
        string aggregation, bool measured, string scalar, string conditional)
    {
        var options = NamedMetricRegistryTests.Options();
        options.NamedMetrics[0].Aggregation = aggregation;
        options.NamedMetrics[0].Measure = measured ? "amount" : "";
        var definition = Assert.Single(NamedMetricRegistryTests.Build(options).Declared);
        var sql = new StringBuilder();
        MetricAggregateSql.Append(sql, definition);
        Assert.Equal("toString(" + scalar + ")", sql.ToString());
        sql.Clear();
        MetricAggregateSql.Append(sql, definition, 7);
        Assert.Equal("toString(" + conditional + ")", sql.ToString());
    }

    [Theory]
    [InlineData("Float64")]
    [InlineData("Decimal")]
    public void FloatingAndDecimalSumsAreNotWidenedToIntegers(string type)
    {
        var options = NamedMetricRegistryTests.Options();
        options.Measures[0].Type = type;
        options.NamedMetrics[0].Measure = "amount";
        options.NamedMetrics[0].Aggregation = "sum";
        var sql = new StringBuilder();
        MetricAggregateSql.Append(sql, Assert.Single(NamedMetricRegistryTests.Build(options).Declared), 0);
        Assert.Equal("toString(coalesce(sumIf(amount, event_type = {reportEvent0:String}), 0))", sql.ToString());
    }

    [Fact]
    public void QueryUsesOneScopedSourceWithOrderedBoundConditionsAndNoPhysicalAliases()
    {
        var options = SavedReportRegistryTests.Options();
        options.NamedMetrics[0].EventType = "a' OR 1=1";
        options.NamedMetrics.Add(new() { Name = "all_events" });
        options.NamedMetrics.Add(new() { Name = "other", EventType = "other" });
        options.SavedReports[0].Fields.Add(new() { Name = "all_rows", Metric = "all_events" });
        options.SavedReports[0].Fields.Add(new() { Name = "other_rows", Metric = "other" });
        var request = Create(options: options, filters: ["country:x' OR 1=1"]).Request!;
        var query = GetSavedReportQuery.BuildQuery(request);
        Assert.Equal("SELECT toString(countIf(event_type = {reportEvent0:String})), toString(count()), "
            + "toString(countIf(event_type = {reportEvent2:String})) FROM nealytics_core.global_events"
            + " WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}"
            + " AND timestamp >= {fromTimestamp:DateTime64} AND timestamp < {toTimestamp:DateTime64}"
            + " AND traffic_class = {trafficClass:String} AND toString(country) = {filter0:String}", query.Sql);
        var parameters = query.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("a' OR 1=1", parameters["reportEvent0"]);
        Assert.Equal("other", parameters["reportEvent2"]);
        Assert.Equal("x' OR 1=1", parameters["filter0"]);
        Assert.Equal(8, parameters.Count);
        Assert.DoesNotContain("a' OR", query.Sql);
        Assert.Equal(request.Scope.From, parameters["fromTimestamp"]);
        Assert.Equal(request.Scope.To, parameters["toTimestamp"]);
        Assert.Equal("project", parameters["projectId"]);
        Assert.Equal("tenant", parameters["tenantId"]);
        Assert.Equal("normal", parameters["trafficClass"]);
    }

    [Fact]
    public void ParameterOrdinalsRemainInvariantAcrossCultures()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal("reportEvent15", MetricAggregateSql.EventTypeParameter(15));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void GeneratedJsonKeepsNullValuesAndExactNumbersWithoutLeakingCompiledDefinitions()
    {
        var defaults = new SavedReportResponse();
        Assert.Equal("", defaults.Name);
        Assert.Equal(0, defaults.Version);
        Assert.Equal(default, defaults.From);
        Assert.Equal(default, defaults.To);
        Assert.Null(defaults.Segment);
        Assert.Empty(defaults.Fields);
        var emptyValue = new SavedReportValue();
        Assert.Equal("", emptyValue.Name);
        Assert.Equal("", emptyValue.Metric);
        Assert.Null(emptyValue.Value);
        Assert.Null(emptyValue.Unit);
        var response = new SavedReportResponse
        {
            Name = "overview", Version = 1, From = Now.AddHours(-1), To = Now, Segment = "selected",
            Fields = [new() { Name = "total", Metric = "sum", Value = "9007199254740993", Unit = "records" },
                new() { Name = "empty", Metric = "average", Value = null }]
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, TelemetryAotContext.Default.SavedReportResponse));
        var root = document.RootElement;
        Assert.Equal("overview", root.GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(Now.AddHours(-1), root.GetProperty("from").GetDateTime());
        Assert.Equal(Now, root.GetProperty("to").GetDateTime());
        Assert.Equal("selected", root.GetProperty("segment").GetString());
        Assert.Equal("string", root.GetProperty("numericEncoding").GetString());
        Assert.Equal("raw", root.GetProperty("source").GetString());
        var fields = root.GetProperty("fields");
        Assert.Equal("total", fields[0].GetProperty("name").GetString());
        Assert.Equal("sum", fields[0].GetProperty("metric").GetString());
        Assert.Equal("9007199254740993", fields[0].GetProperty("value").GetString());
        Assert.Equal("records", fields[0].GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Null, fields[1].GetProperty("value").ValueKind);
        Assert.False(fields[0].TryGetProperty("definition", out _));
    }
}

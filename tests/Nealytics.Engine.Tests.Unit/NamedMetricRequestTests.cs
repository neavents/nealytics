using System.Text;
using System.Text.Json;
using Nealytics.Engine.Features.GetNamedMetric;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed class NamedMetricRequestTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ReadIdentity Identity = new() { ProjectId = "project", TenantId = "tenant" };

    private static NamedMetricRequestResult Create(
        ReadIdentity? identity = null, string name = "activity", string? from = null, string? to = null,
        string? traffic = null, string[]? filters = null, TelemetryEngineOptions? options = null,
        int hours = 24, DateTime? now = null, string? segment = null)
    {
        options ??= NamedMetricRegistryTests.Options();
        var dimensions = new DimensionRegistry(options);
        var measures = new MeasureRegistry(options, dimensions);
        return NamedMetricRequestFactory.Create(identity ?? Identity, name, from, to, traffic, filters ?? [],
            new NamedMetricRegistry(options, measures), new QueryColumns(dimensions), measures, hours, now ?? Now,
            new EventSegmentRegistry(options, new QueryColumns(dimensions), measures), segment);
    }

    [Fact]
    public void DefaultsUseTheConfiguredLookbackAndReturnStoredMillisecondPrecision()
    {
        var result = Create(hours: 7, now: Now.AddTicks(12_345));
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Error);
        Assert.Equal("activity", result.Request!.Definition.Name);
        Assert.Equal(Now.AddMilliseconds(1).AddHours(-7), result.Request.Scope.From);
        Assert.Equal(Now.AddMilliseconds(1), result.Request.Scope.To);
        Assert.Equal(DateTimeKind.Utc, result.Request.Scope.To.Kind);
        Assert.Equal("project", result.Request.Scope.ProjectId);
        Assert.Equal("tenant", result.Request.Scope.TenantId);
        Assert.Equal("normal", result.Request.Scope.TrafficClass);
        Assert.Null(result.Request.Scope.EventType);
        Assert.Null(result.Request.Scope.TenantSet);
        Assert.Empty(result.Request.Scope.Filters);
    }

    [Theory]
    [InlineData("1899-12-31T23:59:59Z", null)]
    [InlineData(null, "2300-01-01T00:00:00Z")]
    [InlineData("2300-01-01T00:00:00Z", null)]
    [InlineData(null, "1899-01-01T00:00:00Z")]
    [InlineData("invalid", null)]
    [InlineData(null, "invalid")]
    [InlineData("2026-10-30T12:00:00Z", "2026-10-30T12:00:00Z")]
    [InlineData("2026-10-30T13:00:00Z", "2026-10-30T12:00:00Z")]
    public void InvalidOrEmptyRangesAreRejected(string? from, string? to)
    {
        var result = Create(from: from, to: to);
        Assert.Null(result.Request);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("Use a valid UTC range [from, to) within years 1900 through 2299.", result.Error);
    }

    [Theory]
    [InlineData("1900-01-01T00:00:00Z", "1900-01-01T00:00:00.001Z")]
    [InlineData("2299-12-31T23:59:59.998Z", "2299-12-31T23:59:59.999Z")]
    public void SupportedCalendarEdgesRemainQueryable(string from, string to)
    {
        Assert.Equal(200, Create(from: from, to: to).StatusCode);
    }

    [Fact]
    public void OffsetsAreNormalizedAndExplicitEmptyValuesUseDefaults()
    {
        var result = Create(from: "2026-10-30T13:00:00.1234567+03:00", to: "2026-10-30T14:00:00.2345678+03:00");
        Assert.Equal(Now.AddHours(-2).AddMilliseconds(123), result.Request!.Scope.From);
        Assert.Equal(Now.AddHours(-1).AddMilliseconds(234), result.Request.Scope.To);
        Assert.Equal(Now, Create(from: "", to: "").Request!.Scope.To);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3_506_329)]
    public void InvalidLookbackCannotOverflowDateArithmetic(int hours)
    {
        Assert.Equal("The configured lookback and current time must fit the supported range.", Create(hours: hours).Error);
    }

    [Theory]
    [InlineData(1899)]
    [InlineData(2300)]
    public void AnUnsupportedClockIsRejectedBeforeLookbackArithmetic(int year)
    {
        Assert.Equal("The configured lookback and current time must fit the supported range.",
            Create(now: new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Error);
    }

    [Fact]
    public void LargestLookbackAndSmallestPositiveLookbackPassTheirOwnValidation()
    {
        Assert.Equal(200, Create(hours: 1).StatusCode);
        Assert.Equal("Use a valid UTC range [from, to) within years 1900 through 2299.", Create(hours: 3_506_328).Error);
        Assert.Equal(200, Create(hours: 3_506_328, from: "1900-01-01", to: "2299-12-31").StatusCode);
    }

    public static TheoryData<ReadIdentity, int, string?> InvalidIdentities => new()
    {
        { new() { Outcome = ReadIdentityOutcome.Forbidden }, 403, null },
        { new() { Outcome = ReadIdentityOutcome.BadRequest, ErrorMessage = "narrowing rejected" }, 400, "narrowing rejected" },
        { new() { ProjectId = " ", TenantId = "tenant" }, 403, null },
        { new() { ProjectId = "project", TenantId = " " }, 403, null },
        { new() { ProjectId = new string('p', 257), TenantId = "tenant" }, 400, "Project and tenant identifiers must not exceed 256 characters." },
        { new() { ProjectId = "project", TenantId = new string('t', 257) }, 400, "Project and tenant identifiers must not exceed 256 characters." }
    };

    [Theory]
    [MemberData(nameof(InvalidIdentities))]
    public void IdentityFailuresDoNotFallThroughToAnUnscopedQuery(ReadIdentity identity, int status, string? error)
    {
        var result = Create(identity);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(error, result.Error);
        Assert.Null(result.Request);
    }

    [Fact]
    public void ExactIdentityLengthAndTenantSetScopeArePreserved()
    {
        var identity = new ReadIdentity
        {
            ProjectId = new string('p', 256), TenantId = null,
            TenantSet = new TenantSet { Attribute = "cohort", Value = "group" }
        };
        var scope = Create(identity).Request!.Scope;
        Assert.Equal(identity.ProjectId, scope.ProjectId);
        Assert.Equal("", scope.TenantId);
        Assert.Equal(identity.TenantSet, scope.TenantSet);
        Assert.Equal(200, Create(new ReadIdentity { ProjectId = "p", TenantId = new string('t', 256) }).StatusCode);
    }

    [Fact]
    public void UnknownNamesAndTrafficAreRejectedAndAllTrafficIsExplicit()
    {
        Assert.Equal(404, Create(name: "missing").StatusCode);
        Assert.Equal("Unknown named metric.", Create(name: "missing").Error);
        Assert.Equal(TrafficFilter.Rejection("unknown"), Create(traffic: "unknown").Error);
        Assert.Null(Create(traffic: "all").Request!.Scope.TrafficClass);
        Assert.Equal("bot", Create(traffic: "bot").Request!.Scope.TrafficClass);
    }

    [Fact]
    public void TypedFiltersHaveABoundedCountAndKeepTheirValuesSeparateFromSql()
    {
        Assert.Equal(200, Create(filters: Enumerable.Repeat("country:x", 32).ToArray()).StatusCode);
        Assert.Equal("At most 32 filters are supported.", Create(filters: Enumerable.Repeat("country:x", 33).ToArray()).Error);
        Assert.Equal(400, Create(filters: ["missing:x"]).StatusCode);
        Assert.Equal(400, Create(filters: ["country:" + new string('x', 257)]).StatusCode);
        Assert.Equal(400, Create(filters: ["amount=nan"]).StatusCode);
        var result = Create(filters: ["country:x' OR 1=1", "amount>=2"]);
        var filters = result.Request!.Scope.Filters;
        Assert.Equal("x' OR 1=1", filters[0].Value);
        Assert.Equal("2", filters[1].Value);
        Assert.Equal(FilterComparison.GreaterOrEqual, filters[1].Comparison);
    }

    [Theory]
    [InlineData("count", 200)]
    [InlineData("sum", 400)]
    public void UnitSafetyCannotBeBypassedByNamingTheMetric(string aggregation, int expected)
    {
        var options = NamedMetricRegistryTests.Options();
        options.NamedMetrics[0].Measure = "amount";
        options.NamedMetrics[0].Aggregation = aggregation;
        var result = Create(options: options);
        Assert.Equal(expected, result.StatusCode);
        Assert.Equal(200, Create(options: options, filters: ["unit_key:u"]).StatusCode);
        if (expected == 400)
        {
            Assert.Contains("filter=unit_key:<value>", result.Error);
        }
    }

    [Theory]
    [InlineData("count", null, "count()")]
    [InlineData("count", "amount", "count(amount)")]
    [InlineData("sum", "amount", "coalesce(sum(toInt256(amount)), 0)")]
    [InlineData("avg", "amount", "avgOrNull(amount)")]
    [InlineData("min", "amount", "minOrNull(amount)")]
    [InlineData("max", "amount", "maxOrNull(amount)")]
    public void QueryUsesOnlyTheCompiledAggregateAndAnExclusiveEnd(string aggregation, string? measure, string expression)
    {
        var options = NamedMetricRegistryTests.Options();
        options.NamedMetrics[0].Aggregation = aggregation;
        options.NamedMetrics[0].Measure = measure!;
        options.NamedMetrics[0].EventType = "action' OR 1=1";
        var request = Create(options: options, filters: ["unit_key:u"]).Request!;
        var query = GetNamedMetricQuery.BuildQuery(request);
        Assert.Equal("SELECT toString(" + expression + ") FROM nealytics_core.global_events"
            + " WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}"
            + " AND timestamp >= {fromTimestamp:DateTime64} AND timestamp < {toTimestamp:DateTime64}"
            + " AND traffic_class = {trafficClass:String} AND event_type = {eventType:String}"
            + " AND toString(unit_key) = {filter0:String}", query.Sql);
        Assert.Equal("action' OR 1=1", query.Parameters.Single(p => p.Key == "eventType").Value);
        Assert.Equal("project", query.Parameters.Single(p => p.Key == "projectId").Value);
        Assert.Equal("tenant", query.Parameters.Single(p => p.Key == "tenantId").Value);
        var legacy = new StringBuilder();
        ScopeClause.AppendRaw(legacy, request.Scope);
        Assert.Contains("timestamp <= {toTimestamp:DateTime64}", legacy.ToString());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("nan", null)]
    [InlineData("inf", null)]
    [InlineData("-inf", null)]
    [InlineData("18446744073709551616", "18446744073709551616")]
    [InlineData("0", "0")]
    public void NumericNormalizationPreservesExactStringsAndMakesNonfiniteResultsNull(string? value, string? expected)
    {
        Assert.Equal(expected, GetNamedMetricQuery.NormalizeValue(value));
    }

    [Fact]
    public void GeneratedSerializationPreservesNumericEncodingAndExplicitNull()
    {
        var response = new NamedMetricResponse
        {
            Name = "activity", Version = 1, From = Now.AddHours(-1), To = Now, Value = null, Unit = "records"
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, TelemetryAotContext.Default.NamedMetricResponse));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("value").ValueKind);
        Assert.Equal("string", json.RootElement.GetProperty("numericEncoding").GetString());
        Assert.Equal("raw", json.RootElement.GetProperty("source").GetString());
        Assert.Equal("activity", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("records", json.RootElement.GetProperty("unit").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(Now, json.RootElement.GetProperty("to").GetDateTime());
        Assert.Equal(Now.AddHours(-1), json.RootElement.GetProperty("from").GetDateTime());
    }

    [Fact]
    public void EventSegmentConditionsIntersectWithCallerFiltersAndPreserveEveryScope()
    {
        var options = EventSegmentRegistryTests.Options();
        options.NamedMetrics[0].EventType = "action";
        var result = Create(options: options, segment: "selected", filters: ["unit_key:v", "country:x"]);
        Assert.Equal(200, result.StatusCode);
        var request = result.Request!;
        Assert.Equal("selected", request.Segment);
        Assert.Equal("project", request.Scope.ProjectId);
        Assert.Equal("tenant", request.Scope.TenantId);
        Assert.Equal("action", request.Scope.EventType);
        Assert.Equal("normal", request.Scope.TrafficClass);
        Assert.Equal(new[] { "unit_key", "amount", "unit_key", "country" }, request.Scope.Filters.Select(f => f.Column));
        Assert.Equal(new[] { "u", "2", "v", "x" }, request.Scope.Filters.Select(f => f.Value));
        var query = GetNamedMetricQuery.BuildQuery(request);
        Assert.Contains("AND toString(unit_key) = {filter0:String} AND amount >= {filter1:Float64}"
            + " AND toString(unit_key) = {filter2:String} AND toString(country) = {filter3:String}", query.Sql);
        Assert.Equal(2d, query.Parameters.Single(p => p.Key == "filter1").Value);
    }

    [Fact]
    public void EventSegmentSelectionCannotBypassUnitSafetyOrOverwriteIdentity()
    {
        var options = EventSegmentRegistryTests.Options();
        options.NamedMetrics[0].Measure = "amount";
        options.NamedMetrics[0].Aggregation = "sum";
        Assert.Equal(400, Create(options: options).StatusCode);
        Assert.Equal(200, Create(options: options, segment: "selected").StatusCode);
        options.EventSegments[0].Filters = ["tenant_id:other"];
        Assert.Equal(400, Create(options: options, segment: "selected").StatusCode);
        var result = Create(options: options, segment: "selected", filters: ["unit_key:u"]);
        Assert.Equal("tenant", result.Request!.Scope.TenantId);
        Assert.Equal("other", result.Request.Scope.Filters[0].Value);
        Assert.Equal(403, Create(identity: new ReadIdentity(), options: options, segment: "missing").StatusCode);
        Assert.Equal("Unknown named metric.", Create(name: "missing", options: options, segment: "missing").Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoEventSegmentKeepsTheOriginalSelection(string? segment)
    {
        var request = Create(segment: segment).Request!;
        Assert.Null(request.Segment);
        Assert.Empty(request.Scope.Filters);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Selected")]
    [InlineData(" selected ")]
    [InlineData(" ")]
    [InlineData("selected,selected")]
    public void UnknownEventSegmentsFailBeforeStorage(string segment)
    {
        var result = Create(options: EventSegmentRegistryTests.Options(), segment: segment);
        Assert.Null(result.Request);
        Assert.Equal(404, result.StatusCode);
        Assert.Equal("Unknown event segment.", result.Error);
    }

    [Fact]
    public void EventSegmentAndRequestBudgetsRemainSeparateAndBounded()
    {
        var options = EventSegmentRegistryTests.Options();
        options.EventSegments[0].Filters = Enumerable.Repeat("country:x", 32).ToList();
        Assert.Equal(64, Create(options: options, segment: "selected",
            filters: Enumerable.Repeat("country:y", 32).ToArray()).Request!.Scope.Filters.Count);
        Assert.Equal("At most 32 filters are supported.", Create(options: options, segment: "selected",
            filters: Enumerable.Repeat("country:y", 33).ToArray()).Error);
    }

    [Fact]
    public void ASelectedEventSegmentCanHaveFewerConditionsThanTheCaller()
    {
        var options = EventSegmentRegistryTests.Options();
        options.EventSegments[0].Filters = ["unit_key:u"];
        var request = Create(options: options, segment: "selected", filters: ["country:x", "amount>2"]).Request!;
        Assert.Equal(new[] { "unit_key", "country", "amount" }, request.Scope.Filters.Select(f => f.Column));
        var query = GetNamedMetricQuery.BuildQuery(request);
        Assert.Equal("u", query.Parameters.Single(p => p.Key == "filter0").Value);
        Assert.Equal("x", query.Parameters.Single(p => p.Key == "filter1").Value);
        Assert.Equal(2d, query.Parameters.Single(p => p.Key == "filter2").Value);
    }
}

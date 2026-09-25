using FluentAssertions;
using Nealytics.Engine.Features.GetComparison;
using Nealytics.Engine.Features.GetPivot;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public class ComparisonTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private const string CurrentWindow = "(timestamp >= {fromTimestamp:DateTime64} AND timestamp < {toTimestamp:DateTime64})";
    private const string PreviousWindow = "(timestamp >= {previousFrom:DateTime64} AND timestamp < {previousTo:DateTime64})";

    private static TelemetryEngineOptions Options(params RollupOptions[] rollups) => new()
    {
        Dimensions =
        [
            new DimensionOptions { Name = "widget_id", Type = "String" },
            new DimensionOptions { Name = "shelf", Type = "LowCardinality" },
        ],
        Measures =
        [
            new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "sum,avg,p95" },
        ],
        Rollups = [.. rollups],
    };

    private static (QueryColumns Columns, MeasureRegistry Measures, RollupRegistry Rollups) Registries(params RollupOptions[] rollups)
    {
        TelemetryEngineOptions options = Options(rollups);
        DimensionRegistry dimensions = new(options);
        MeasureRegistry measures = new(options, dimensions);
        return (new QueryColumns(dimensions), measures, new RollupRegistry(options, dimensions, measures));
    }

    private static ComparisonRequestResult Create(
        string[]? metrics = null,
        string? groupBy = null,
        string[]? filters = null,
        string? from = "2026-09-14T00:00:00Z",
        string? to = "2026-09-21T00:00:00Z",
        string? previousFrom = null,
        string? previousTo = null,
        string? tz = null,
        string? traffic = null,
        string? orderBy = null,
        string? order = null,
        string? exact = null,
        string? mode = null,
        string? limit = null,
        string? projectId = "proj",
        string? tenantId = "tenant")
    {
        (QueryColumns columns, MeasureRegistry measures, _) = Registries();
        return ComparisonRequestFactory.Create(
            new ComparisonQueryParameters
            {
                ProjectId = projectId,
                TenantId = tenantId,
                Metrics = metrics ?? ["events"],
                GroupBy = groupBy,
                Filters = filters ?? [],
                From = from,
                To = to,
                PreviousFrom = previousFrom,
                PreviousTo = previousTo,
                TimeZone = tz,
                Traffic = traffic,
                OrderBy = orderBy,
                Order = order,
                Exact = exact,
                Mode = mode,
                Limit = limit,
            },
            columns, measures, 10_000, 24, Now);
    }

    [Fact]
    public void ThePreviousWindowDefaultsToTheOneOfEqualLengthJustBefore()
    {
        ComparisonRequestResult result = Create();

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Request.From.Should().Be(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));
        result.Request.To.Should().Be(new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc));
        result.Request.PreviousFrom.Should().Be(new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc));
        result.Request.PreviousTo.Should().Be(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void AnExplicitPreviousWindowIsKept()
    {
        ComparisonRequestResult result = Create(
            previousFrom: "2025-09-14T00:00:00Z", previousTo: "2025-09-21T00:00:00Z");

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Request.PreviousFrom.Should().Be(new DateTime(2025, 9, 14, 0, 0, 0, DateTimeKind.Utc));
        result.Request.PreviousTo.Should().Be(new DateTime(2025, 9, 21, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void WithoutFromOrToTheDefaultRangeEndsNow()
    {
        ComparisonRequestResult result = Create(from: null, to: null);

        result.Request.From.Should().Be(Now.AddHours(-24));
        result.Request.To.Should().Be(Now);
        result.Request.PreviousFrom.Should().Be(Now.AddHours(-48));
    }

    [Fact]
    public void ANaiveTimestampIsReadInTheRequestedZone()
    {
        ComparisonRequestResult result = Create(from: "2026-09-14T00:00:00", to: "2026-09-21T00:00:00", tz: "Europe/Istanbul");

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Request.From.Should().Be(new DateTime(2026, 9, 13, 21, 0, 0, DateTimeKind.Utc));
        result.Request.To.Should().Be(new DateTime(2026, 9, 20, 21, 0, 0, DateTimeKind.Utc));
        result.Request.TimeZone.Should().Be("Europe/Istanbul");
    }

    [Fact]
    public void AnOffsetWinsOverTheZone()
    {
        ComparisonRequestResult result = Create(from: "2026-09-14T00:00:00Z", to: "2026-09-21T00:00:00+02:00", tz: "Europe/Istanbul");

        result.Request.From.Should().Be(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));
        result.Request.To.Should().Be(new DateTime(2026, 9, 20, 22, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ThePrecedingWindowFollowsTheWallClockAcrossADaylightSavingChange()
    {
        ComparisonRequestResult result = Create(
            from: "2026-11-02T00:00:00", to: "2026-11-09T00:00:00", tz: "America/New_York");

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Request.From.Should().Be(new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc));
        result.Request.PreviousFrom.Should().Be(new DateTime(2026, 10, 26, 4, 0, 0, DateTimeKind.Utc));
        result.Request.PreviousTo.Should().Be(result.Request.From);
    }

    [Theory]
    [InlineData("Not/AZone")]
    [InlineData("Europe/Istanbul;DROP")]
    public void AnUnknownZoneIsRefused(string tz)
    {
        ComparisonRequestResult result = Create(tz: tz);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("'tz'");
    }

    [Theory]
    [InlineData("2026-09-21T00:00:00Z", "2026-09-21T00:00:00Z")]
    [InlineData("2026-09-22T00:00:00Z", "2026-09-21T00:00:00Z")]
    [InlineData("yesterday", "2026-09-21T00:00:00Z")]
    [InlineData("2026-09-21T00:00:00Z", "later")]
    public void AnEmptyInvertedOrUnreadableWindowIsRefused(string from, string to)
    {
        Create(from: from, to: to).Success.Should().BeFalse();
    }

    [Theory]
    [InlineData("2025-09-14T00:00:00Z", null)]
    [InlineData(null, "2025-09-14T00:00:00Z")]
    [InlineData("2025-09-21T00:00:00Z", "2025-09-14T00:00:00Z")]
    [InlineData("soon", "2025-09-14T00:00:00Z")]
    public void AnIncompleteOrInvertedPreviousWindowIsRefused(string? previousFrom, string? previousTo)
    {
        Create(previousFrom: previousFrom, previousTo: previousTo).Success.Should().BeFalse();
    }

    [Fact]
    public void ExactlyOneMetricIsRequiredInThePivotGrammar()
    {
        Create(metrics: []).Success.Should().BeFalse();
        Create(metrics: ["events", "sessions"]).Success.Should().BeFalse();
        Create(metrics: ["median(dwell_ms)"]).Success.Should().BeFalse();
        Create(metrics: [new string('x', 257)]).Success.Should().BeFalse();
        Create(metrics: ["p95(dwell_ms):view"]).Request.Metric.AggregationParameters.Should().Be("0.95");
    }

    [Fact]
    public void GroupByAndFiltersUseTheAllowlist()
    {
        Create(groupBy: "nope").Success.Should().BeFalse();
        Create(filters: ["nope:1"]).Success.Should().BeFalse();
        Create(filters: Enumerable.Repeat("shelf:a", ComparisonRequestFactory.MaxFilters + 1).ToArray()).Success.Should().BeFalse();
        Create(groupBy: "shelf").Request.GroupByColumn.Should().Be("shelf");
    }

    [Fact]
    public void MissingClaimsAreForbiddenAndLongClaimsRefused()
    {
        Create(projectId: null).ErrorStatusCode.Should().Be(403);
        Create(tenantId: " ").ErrorStatusCode.Should().Be(403);
        Create(projectId: new string('p', 257)).ErrorStatusCode.Should().Be(400);
    }

    [Fact]
    public void OrderingDefaultsToCurrentDescendingAndKeyAscending()
    {
        Create().Request.Order.Should().Be(ComparisonOrder.Current);
        Create().Request.Descending.Should().BeTrue();
        Create(orderBy: "key").Request.Descending.Should().BeFalse();
        Create(orderBy: "change", order: "asc").Request.Descending.Should().BeFalse();
        Create(orderBy: "previous").Request.Order.Should().Be(ComparisonOrder.Previous);
        Create(orderBy: "relative").Success.Should().BeFalse();
        Create(order: "up").Success.Should().BeFalse();
        Create(traffic: "robots").Success.Should().BeFalse();
    }

    [Fact]
    public void UngroupedRawSqlIsExact()
    {
        ComparisonRequestResult parsed = Create(metrics: ["sessions:view"], filters: ["shelf:top"]);

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = GetComparisonQuery.BuildQuery(parsed.Request, null);

        sql.Should().Be(
            "SELECT uniqExactIf(session_id, " + CurrentWindow + ") AS c, uniqExactIf(session_id, " + PreviousWindow + ") AS p"
            + " FROM nealytics_core.global_events"
            + " WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}"
            + " AND (" + CurrentWindow + " OR " + PreviousWindow + ")"
            + " AND traffic_class = {trafficClass:String} AND event_type = {eventType:String}"
            + " AND toString(shelf) = {filter0:String}");

        parameters.Select(p => p.Key).Should().BeEquivalentTo(
            ["projectId", "tenantId", "fromTimestamp", "toTimestamp", "trafficClass", "eventType", "filter0", "previousFrom", "previousTo"]);
        parameters.Should().Contain(p => p.Key == "eventType" && Equals(p.Value, "view"));
        parameters.Should().Contain(p => p.Key == "previousFrom" && Equals(p.Value, new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void GroupedRawSqlIsExact()
    {
        ComparisonRequestResult parsed = Create(metrics: ["p95(dwell_ms)"], groupBy: "widget_id", orderBy: "change", traffic: "all");

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = GetComparisonQuery.BuildQuery(parsed.Request, null);

        string aggregates = "quantileIf(0.95)(dwell_ms, " + CurrentWindow + ") AS c, quantileIf(0.95)(dwell_ms, " + PreviousWindow + ") AS p";
        string where = " WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}"
            + " AND (" + CurrentWindow + " OR " + PreviousWindow + ")";

        sql.Should().Be(
            "WITH grouped AS (SELECT ifNull(toString(widget_id), '') AS key, " + aggregates
            + " FROM nealytics_core.global_events" + where + " GROUP BY key),"
            + " totals AS (SELECT " + aggregates + " FROM nealytics_core.global_events" + where + ")"
            + " SELECT key, c, p, (SELECT c FROM totals) AS tc, (SELECT p FROM totals) AS tp,"
            + " (SELECT count() FROM grouped) AS group_count FROM grouped ORDER BY (c - p) DESC, key ASC LIMIT {limit:Int32}");
        parameters.Should().Contain(p => p.Key == "limit" && Equals(p.Value, ComparisonRequestFactory.DefaultLimit));
        parameters.Should().NotContain(p => p.Key == "trafficClass");
    }

    [Theory]
    [InlineData("events", "false", "count()")]
    [InlineData("events", "true", "uniqExact(event_id)")]
    [InlineData("users", "false", "uniqExact(user_id)")]
    [InlineData("distinct(shelf)", "false", "uniqExact(shelf)")]
    [InlineData("avg(dwell_ms)", "false", "avg(dwell_ms)")]
    public void EveryMetricKindIsScopedByTheWindow(string metric, string exact, string unscoped)
    {
        ComparisonRequestResult parsed = Create(metrics: [metric], exact: exact);

        (string sql, _) = GetComparisonQuery.BuildQuery(parsed.Request, null);

        string function = unscoped[..unscoped.IndexOf('(')];
        string argument = unscoped[(unscoped.IndexOf('(') + 1)..^1];
        string expected = argument.Length == 0
            ? function + "If(" + CurrentWindow + ") AS c"
            : function + "If(" + argument + ", " + CurrentWindow + ") AS c";
        sql.Should().StartWith("SELECT " + expected);
    }

    [Fact]
    public void ApproximateModeUsesUniq()
    {
        (string sql, _) = GetComparisonQuery.BuildQuery(Create(metrics: ["sessions"], mode: "approx").Request, null);

        sql.Should().StartWith("SELECT uniqIf(session_id, ");
    }

    private static readonly RollupOptions Daily = new()
    {
        Name = "daily_by_widget",
        Grain = "day",
        EventTypes = "view",
        Dimensions = "widget_id",
        Measures = "dwell_ms:sum",
    };

    [Fact]
    public void AlignedWindowsRouteToTheRollupAndMergeByBucket()
    {
        (QueryColumns _, MeasureRegistry _, RollupRegistry rollups) = Registries(Daily);
        ComparisonRequest request = Create(metrics: ["sessions:view"], groupBy: "widget_id").Request;

        PivotRollupPlan? plan = GetComparisonQuery.Plan(request, rollups);

        plan.Should().NotBeNull();
        (string sql, _) = GetComparisonQuery.BuildQuery(request, plan);
        sql.Should().Contain("SELECT widget_id AS key, uniqExactMergeIf(sessions, (bucket >= {fromTimestamp:DateTime64} AND bucket < {toTimestamp:DateTime64})) AS c");
        sql.Should().Contain("uniqExactMergeIf(sessions, (bucket >= {previousFrom:DateTime64} AND bucket < {previousTo:DateTime64})) AS p");
        sql.Should().Contain("FROM nealytics_core.");
        sql.Should().Contain("AND event_type = {eventType:String}");
        sql.Should().NotContain("global_events");
    }

    [Fact]
    public void AnUngroupedAlignedComparisonRoutesToo()
    {
        (_, _, RollupRegistry rollups) = Registries(Daily);
        ComparisonRequest request = Create(metrics: ["sum(dwell_ms):view"]).Request;

        PivotRollupPlan? plan = GetComparisonQuery.Plan(request, rollups);

        plan.Should().NotBeNull();
        (string sql, _) = GetComparisonQuery.BuildQuery(request, plan);
        sql.Should().StartWith("SELECT sumMergeIf(");
    }

    [Fact]
    public void AMisalignedPreviousWindowKeepsTheWholeComparisonOnRaw()
    {
        (_, _, RollupRegistry rollups) = Registries(Daily);
        ComparisonRequest request = Create(
            metrics: ["events:view"], previousFrom: "2026-09-07T01:00:00Z", previousTo: "2026-09-14T00:00:00Z").Request;

        GetComparisonQuery.Plan(request, rollups).Should().BeNull();
    }

    [Fact]
    public void AnUnroutableMetricStaysOnRaw()
    {
        (_, _, RollupRegistry rollups) = Registries(Daily);

        GetComparisonQuery.Plan(Create(metrics: ["p95(dwell_ms):view"]).Request, rollups).Should().BeNull();
        GetComparisonQuery.Plan(Create(metrics: ["events:click"]).Request, rollups).Should().BeNull();
        GetComparisonQuery.Plan(Create(metrics: ["events:view"], exact: "true").Request, rollups).Should().BeNull();
    }

    [Fact]
    public void ChangeAndRelativeChangeAreDerivedFromTheTwoValues()
    {
        ComparisonValue grew = ComparisonValue.Of(15, 10);
        ComparisonValue fromNothing = ComparisonValue.Of(4, 0);
        ComparisonValue unmeasured = ComparisonValue.Of(double.NaN, 3);

        grew.Change.Should().Be(5);
        grew.RelativeChange.Should().Be(0.5);
        fromNothing.Change.Should().Be(4);
        fromNothing.RelativeChange.Should().BeNull();
        unmeasured.Current.Should().BeNull();
        unmeasured.Change.Should().BeNull();
        unmeasured.RelativeChange.Should().BeNull();
    }
}

using FluentAssertions;
using Nealytics.Engine.Features.GetUnseenObjects;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public class UnseenObjectsTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private const string Scope =
        " WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}"
        + " AND timestamp >= {fromTimestamp:DateTime64} AND timestamp <= {toTimestamp:DateTime64}"
        + " AND traffic_class = {trafficClass:String}";

    private static TelemetryEngineOptions Options(params RollupOptions[] rollups) => new()
    {
        Dimensions = [new DimensionOptions { Name = "shelf", Type = "LowCardinality" }],
        Measures = [new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "avg" }],
        Rollups = [.. rollups],
    };

    private static (QueryColumns Columns, MeasureRegistry Measures, RollupRegistry Rollups) Registries(params RollupOptions[] rollups)
    {
        TelemetryEngineOptions options = Options(rollups);
        DimensionRegistry dimensions = new(options);
        MeasureRegistry measures = new(options, dimensions);
        return (new QueryColumns(dimensions), measures, new RollupRegistry(options, dimensions, measures));
    }

    private static UnseenObjectsRequestResult Create(
        UnseenObjectsBody? body, string? projectId = "proj", string? tenantId = "tenant")
    {
        (QueryColumns columns, MeasureRegistry measures, _) = Registries();
        return UnseenObjectsRequestFactory.Create(projectId, tenantId, body, columns, measures, 24, Now);
    }

    private static UnseenObjectsBody Body(
        string[]? ids = null,
        string? eventType = null,
        string? impression = null,
        string[]? filter = null,
        string? from = "2026-09-01T00:00:00Z",
        string? to = "2026-09-08T00:00:00Z",
        string? traffic = null) => new()
    {
        Ids = ids ?? ["a", "b"],
        EventType = eventType,
        ImpressionEventType = impression,
        Filter = filter,
        From = from,
        To = to,
        Traffic = traffic,
    };

    [Fact]
    public void IdsAreDeduplicatedInOrder()
    {
        UnseenObjectsRequestResult result = Create(Body(ids: ["b", "a", "b", "c", "a"]));

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Request.Ids.Should().Equal("b", "a", "c");
    }

    [Fact]
    public void TheCapIsAcceptedAndOneMoreIsRefused()
    {
        string[] atCap = Enumerable.Range(0, UnseenObjectsRequestFactory.MaxIds).Select(i => "id" + i).ToArray();
        string[] overCap = [.. atCap, "extra"];

        Create(Body(ids: atCap)).Request.Ids.Should().HaveCount(5_000);
        Create(Body(ids: overCap)).Success.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankIdIsRefusedByPosition(string? blank)
    {
        UnseenObjectsRequestResult result = Create(Body(ids: ["a", blank!]));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ids[1]");
    }

    [Fact]
    public void AMalformedBodyIsRefused()
    {
        Create(null).Success.Should().BeFalse();
        Create(new UnseenObjectsBody()).Success.Should().BeFalse();
        Create(Body(ids: [])).Success.Should().BeFalse();
        Create(Body(ids: [new string('x', 257)])).Success.Should().BeFalse();
        Create(Body(eventType: "view", impression: "view")).Success.Should().BeFalse();
        Create(Body(eventType: new string('e', 257))).Success.Should().BeFalse();
        Create(Body(from: "2026-09-09T00:00:00Z")).Success.Should().BeFalse();
        Create(Body(filter: ["nope:1"])).Success.Should().BeFalse();
        Create(Body(traffic: "robots")).Success.Should().BeFalse();
        Create(Body(filter: Enumerable.Repeat("shelf:a", 17).ToArray())).Success.Should().BeFalse();
    }

    [Fact]
    public void MissingClaimsAreForbidden()
    {
        Create(Body(), projectId: null).ErrorStatusCode.Should().Be(403);
        Create(Body(), tenantId: "").ErrorStatusCode.Should().Be(403);
    }

    [Fact]
    public void EngagementAndImpressionSqlIsExact()
    {
        UnseenObjectsRequest request = Create(Body(eventType: "view", impression: "impression", filter: ["shelf:top"])).Request;

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) = GetUnseenObjectsQuery.BuildQuery(request, null);

        sql.Should().Be(
            "SELECT candidate_ids.id, seen.exposed FROM candidate_ids LEFT ANY JOIN (SELECT assumeNotNull(object_id) AS id,"
            + " max(event_type = {eventType:String}) AS engaged, max(event_type = {impressionEventType:String}) AS exposed"
            + " FROM nealytics_core.global_events" + Scope + " AND toString(shelf) = {filter0:String}"
            + " AND event_type IN ({eventType:String}, {impressionEventType:String})"
            + " AND object_id IN (SELECT id FROM candidate_ids) GROUP BY id) AS seen"
            + " ON candidate_ids.id = seen.id WHERE seen.engaged = 0 ORDER BY candidate_ids.id");
        parameters.Should().Contain(p => p.Key == "eventType" && Equals(p.Value, "view"));
        parameters.Should().Contain(p => p.Key == "impressionEventType" && Equals(p.Value, "impression"));
        parameters.Should().Contain(p => p.Key == "filter0" && Equals(p.Value, "top"));
        sql.Should().NotContain("'view'").And.NotContain("'a'");
    }

    [Fact]
    public void AnEngagementTypeAloneNarrowsTheScanToIt()
    {
        UnseenObjectsRequest request = Create(Body(eventType: "view")).Request;

        (string sql, _) = GetUnseenObjectsQuery.BuildQuery(request, null);

        sql.Should().Contain("max(1) AS engaged, max(0) AS exposed");
        sql.Should().Contain(Scope + " AND event_type = {eventType:String} AND object_id IN");
    }

    [Fact]
    public void WithoutAnyTypeEveryEventIsASighting()
    {
        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) =
            GetUnseenObjectsQuery.BuildQuery(Create(Body()).Request, null);

        sql.Should().Contain("max(1) AS engaged, max(0) AS exposed");
        sql.Should().NotContain("event_type");
        parameters.Should().NotContain(p => p.Key == "eventType" || p.Key == "impressionEventType");
    }

    [Fact]
    public void WithOnlyAnImpressionTypeAnyOtherEventIsEngagement()
    {
        (string sql, _) = GetUnseenObjectsQuery.BuildQuery(Create(Body(impression: "impression")).Request, null);

        sql.Should().Contain(
            "max(event_type != {impressionEventType:String}) AS engaged, max(event_type = {impressionEventType:String}) AS exposed");
        sql.Should().NotContain("event_type IN");
    }

    private static readonly RollupOptions ByObject = new()
    {
        Name = "daily_by_object",
        Grain = "day",
        EventTypes = "impression,view",
        Dimensions = "object_id,shelf",
        Measures = "",
    };

    [Fact]
    public void AnAlignedCoveredReadRoutesToTheRollup()
    {
        (_, _, RollupRegistry rollups) = Registries(ByObject);
        UnseenObjectsRequest request = Create(Body(eventType: "view", impression: "impression", filter: ["shelf:top"])).Request;

        Nealytics.Engine.Infrastructure.Configuration.Rollup? plan = GetUnseenObjectsQuery.Plan(request, rollups);

        plan.Should().NotBeNull();
        (string sql, _) = GetUnseenObjectsQuery.BuildQuery(request, plan);
        sql.Should().Contain("FROM nealytics_core." + plan!.TableName
            + " WHERE project_id = {projectId:String} AND tenant_id = {tenantId:String}"
            + " AND bucket >= {fromTimestamp:DateTime64} AND bucket < {toTimestamp:DateTime64}"
            + " AND traffic_class = {trafficClass:String} AND shelf = {filter0:String}");
    }

    [Fact]
    public void AReadTheRollupCannotAnswerStaysRaw()
    {
        (_, _, RollupRegistry rollups) = Registries(ByObject);

        GetUnseenObjectsQuery.Plan(Create(Body(eventType: "click")).Request, rollups).Should().BeNull();
        GetUnseenObjectsQuery.Plan(Create(Body()).Request, rollups).Should().BeNull();
        GetUnseenObjectsQuery.Plan(Create(Body(eventType: "view", to: "2026-09-08T01:00:00Z")).Request, rollups).Should().BeNull();
        GetUnseenObjectsQuery.Plan(Create(Body(eventType: "view", filter: ["dwell_ms>5"])).Request, rollups).Should().BeNull();
    }

    [Fact]
    public void TheCandidateTableCarriesEveryIdAsOneStringColumn()
    {
        Octonica.ClickHouseClient.ClickHouseTableProvider table = GetUnseenObjectsQuery.Candidates(["a", "b", "c"]);

        table.TableName.Should().Be("candidate_ids");
        table.RowCount.Should().Be(3);
        table.ColumnCount.Should().Be(1);
    }
}

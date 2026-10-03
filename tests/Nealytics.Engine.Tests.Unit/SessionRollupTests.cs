using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Features.GetSessionAnalytics;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// One row per session instead of per time bucket.
///
/// <b>Why this replaces a scheduled sessionizer.</b> The obvious design is a job that sweeps
/// sessions idle for thirty minutes and writes them out. That job has to be re-run for late
/// arrivals, and still leaves a hole when one lands after the re-run — so "how long was that
/// session" has a correct answer that depends on when you asked. An AggregatingMergeTree fed by a
/// materialized view cannot have that hole: a late event is just another partial state that merges
/// in. "Session ended" then needs no idle rule at all, it is maxMerge(timestamp).
///
/// <b>Verified against ClickHouse 26.7.1, not reasoned about.</b> On a probe database: a session
/// spanning 23:50–00:05 was stored as two rows and regrouped to a single 900-second session; an
/// event inserted after the fact extended an already-"finished" session from 360s to 540s with no
/// re-run; and bounce rate came out of uniqExactMerge at read time. The shapes below are the ones
/// that produced those results.
/// </summary>
public class SessionRollupTests
{
    private static Rollup Sessions(string dimensions = "article_id,locale", string measures = "dwell_ms:sum,dwell_ms:max")
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions =
            [
                new DimensionOptions { Name = "article_id", Type = "String" },
                new DimensionOptions { Name = "locale", Type = "LowCardinality" },
            ],
            Measures = [new MeasureOptions { Name = "dwell_ms", Type = "UInt32", Aggregations = "sum,max" }],
            Rollups =
            [
                new RollupOptions
                {
                    Name = "sessions", Grain = "session", Dimensions = dimensions, Measures = measures,
                },
            ],
        };

        DimensionRegistry dimensionRegistry = new(options);
        MeasureRegistry measureRegistry = new(options, dimensionRegistry);

        return new RollupRegistry(options, dimensionRegistry, measureRegistry).Declared[0];
    }

    [Fact]
    public void SessionIsARecognisedGrain()
    {
        Sessions().Grain.Should().Be(RollupGrain.Session);
    }

    [Fact]
    public void AnUnknownGrainNamesTheOnesThatExist()
    {
        TelemetryEngineOptions options = new()
        {
            Rollups = [new RollupOptions { Name = "wrong", Grain = "fortnight" }],
        };

        DimensionRegistry dimensions = new(options);

        Action act = () => new RollupRegistry(options, dimensions, new MeasureRegistry(options, dimensions));

        act.Should().Throw<InvalidOperationException>().WithMessage("*hour, day, session*");
    }

    [Fact]
    public void TheTableKeysOnTheSessionAndAPlainDate()
    {
        string ddl = RollupRegistry.BuildTableDdl(Sessions());

        // A plain Date rather than the session's start, because a partition key must be a function
        // of the ORDER BY columns and min(timestamp) is an aggregate — there is nothing to
        // partition by. A session crossing midnight therefore becomes two rows, which the read side
        // regroups.
        ddl.Should().Contain("event_date Date");
        ddl.Should().Contain("PARTITION BY toYYYYMM(event_date)");
        ddl.Should().Contain("ORDER BY (project_id, tenant_id, event_date, session_id, traffic_class, article_id, locale)");
    }

    [Fact]
    public void TheTableDoesNotKeyOnEventType()
    {
        string ddl = RollupRegistry.BuildTableDdl(Sessions());

        // Every time-bucketed rollup keys on event_type. A session rollup must not: a session
        // touching five event types would become five rows, and its duration would be measured per
        // event type rather than per session — a number that looks entirely reasonable and answers
        // a question nobody asked.
        ddl.Should().NotContain("event_type LowCardinality(String),");
        ddl.Should().NotContain(", event_type)");
    }

    [Fact]
    public void TheTableHoldsTheTwoFactsAJobWouldHaveComputed()
    {
        string ddl = RollupRegistry.BuildTableDdl(Sessions());

        ddl.Should().Contain("started_at AggregateFunction(min, DateTime64(3, 'UTC'))");
        ddl.Should().Contain("ended_at AggregateFunction(max, DateTime64(3, 'UTC'))");
    }

    [Fact]
    public void BounceIsAStateRatherThanAFrozenFlag()
    {
        string table = RollupRegistry.BuildTableDdl(Sessions());
        string view = RollupRegistry.BuildViewDdl(Sessions());

        // Storing is_bounce would bake one product's threshold into storage. Keeping the distinct
        // event-type count as a state lets the definition change without rebuilding the table.
        table.Should().Contain("event_types AggregateFunction(uniqExact, String)");
        view.Should().Contain("uniqExactState(event_type) AS event_types");
        table.Should().NotContain("is_bounce");
    }

    [Fact]
    public void TheViewGroupsByTheSessionNotByATimeBucket()
    {
        string view = RollupRegistry.BuildViewDdl(Sessions());

        view.Should().Contain("toDate(timestamp) AS event_date");
        view.Should().Contain("GROUP BY project_id, tenant_id, event_date, session_id, traffic_class, article_id, locale");
        view.Should().NotContain("toStartOfDay");
        view.Should().NotContain("toStartOfHour");
    }

    [Fact]
    public void NullableDimensionsAreNormalisedTheSameWayEverythingElseIs()
    {
        string view = RollupRegistry.BuildViewDdl(Sessions());

        // AggregatingMergeTree refuses a nullable sorting key outright, and this is also exactly how
        // /breakdown renders a key — so the two agree by construction rather than by luck.
        view.Should().Contain("ifNull(toString(article_id), '') AS article_id");
    }

    [Fact]
    public void MeasureStatesCarryTheirDeclaredType()
    {
        string table = RollupRegistry.BuildTableDdl(Sessions());
        string view = RollupRegistry.BuildViewDdl(Sessions());

        table.Should().Contain("dwell_ms_sum AggregateFunction(sum, Nullable(UInt32))");
        table.Should().Contain("dwell_ms_max AggregateFunction(max, Nullable(UInt32))");
        view.Should().Contain("sumState(dwell_ms) AS dwell_ms_sum");
        view.Should().Contain("maxState(dwell_ms) AS dwell_ms_max");
    }

    [Fact]
    public void ARollupWithNoDimensionsIsStillOnePerSession()
    {
        string ddl = RollupRegistry.BuildTableDdl(Sessions(dimensions: "", measures: ""));

        ddl.Should().Contain("ORDER BY (project_id, tenant_id, event_date, session_id, traffic_class)");
    }

    [Fact]
    public void ABreakdownNeverRoutesToASessionRollup()
    {
        // The table has no `bucket` column and does not key on event_type, so the query the planner
        // builds would not merely be slow against it — it would not be the same question. A day
        // aligned range is the case most likely to slip through, so that is the one asserted.
        RollupPlanner.IsAligned(
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
            RollupGrain.Session)
            .Should().BeFalse();
    }

    [Fact]
    public void TheBucketedGrainsStillBuildWhatTheyAlwaysDid()
    {
        // The session branch must not have changed the shape of the rollups already in production.
        TelemetryEngineOptions options = new()
        {
            Dimensions = [new DimensionOptions { Name = "article_id", Type = "String" }],
            Rollups = [new RollupOptions { Name = "daily", Grain = "day", Dimensions = "article_id" }],
        };

        DimensionRegistry dimensions = new(options);
        Rollup daily = new RollupRegistry(options, dimensions, new MeasureRegistry(options, dimensions)).Declared[0];

        string ddl = RollupRegistry.BuildTableDdl(daily);

        ddl.Should().Contain("bucket DateTime('UTC')");
        ddl.Should().Contain("event_type LowCardinality(String)");
        ddl.Should().Contain("PARTITION BY toYYYYMM(bucket)");
        RollupRegistry.BuildViewDdl(daily).Should().Contain("toStartOfDay(timestamp) AS bucket");
    }
}

/// <summary>
/// When /sessions is allowed to read the rollup instead of scanning raw.
///
/// Storage without routing buys nothing, and routing without these three refusals buys a wrong
/// answer that looks healthy. Each condition below is a way the response would have silently
/// stopped meaning what it says.
/// </summary>
public class SessionRollupRoutingTests
{
    private static RollupRegistry Registry(string grain = "session", string eventTypes = "")
    {
        TelemetryEngineOptions options = new()
        {
            Dimensions = [new DimensionOptions { Name = "article_id", Type = "String" }],
            Rollups =
            [
                new RollupOptions
                {
                    Name = "sessions", Grain = grain, Dimensions = "article_id", EventTypes = eventTypes,
                },
            ],
        };

        DimensionRegistry dimensions = new(options);
        return new RollupRegistry(options, dimensions, new MeasureRegistry(options, dimensions));
    }

    private static RollupRegistry Empty()
    {
        TelemetryEngineOptions options = new();
        DimensionRegistry dimensions = new(options);
        return new RollupRegistry(options, dimensions, new MeasureRegistry(options, dimensions));
    }

    private static SessionAnalyticsRequest Request(DateTime from, DateTime to) =>
        new() { ProjectId = "p", TenantId = "t", From = from, To = to, Limit = 100 };

    private static readonly DateTime Midnight = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NextWeek = new(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ADayAlignedRangeRoutesToTheRollup()
    {
        GetSessionAnalyticsQuery.SelectRollup(Request(Midnight, NextWeek), Registry())
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData(12, 0, 0)]
    [InlineData(0, 30, 0)]
    [InlineData(0, 0, 1)]
    public void ARangeThatDoesNotStartOnMidnightStaysOnRaw(int hour, int minute, int second)
    {
        // A rollup row covers a whole day. Answering 12:00-18:00 from it would return whole days —
        // a number that is wrong and looks completely healthy, which is the worst kind.
        GetSessionAnalyticsQuery.SelectRollup(
            Request(Midnight.AddHours(hour).AddMinutes(minute).AddSeconds(second), NextWeek), Registry())
            .Should().BeNull();
    }

    [Fact]
    public void ARangeThatDoesNotEndOnMidnightStaysOnRaw()
    {
        GetSessionAnalyticsQuery.SelectRollup(Request(Midnight, NextWeek.AddHours(9)), Registry())
            .Should().BeNull();
    }

    [Fact]
    public void ARollupThatFiltersEventTypesIsNotUsed()
    {
        // It holds only those rows, so its per-session event count is not the session's event
        // count. A perfectly good rollup that answers a different question.
        GetSessionAnalyticsQuery.SelectRollup(
            Request(Midnight, NextWeek), Registry(eventTypes: "app_open,article_view"))
            .Should().BeNull();
    }

    [Fact]
    public void ATimeBucketedRollupIsNotUsed()
    {
        GetSessionAnalyticsQuery.SelectRollup(Request(Midnight, NextWeek), Registry(grain: "day"))
            .Should().BeNull();
    }

    [Fact]
    public void WithNoRollupDeclaredRawIsTheAnswerRatherThanAFallback()
    {
        GetSessionAnalyticsQuery.SelectRollup(Request(Midnight, NextWeek), Empty()).Should().BeNull();
    }

    [Fact]
    public void AnInvertedRangeStaysOnRaw()
    {
        GetSessionAnalyticsQuery.SelectRollup(Request(NextWeek, Midnight), Registry()).Should().BeNull();
    }

    [Fact]
    public void TheRollupStatementRegroupsBySessionSoMidnightCrossersRecombine()
    {
        (string sql, _) = GetSessionAnalyticsQuery.BuildQuery(Request(Midnight, NextWeek), Registry().Declared[0]);

        // GROUP BY session_id WITHOUT event_date is the whole trick: the session is stored as one
        // row per day, and minMerge/maxMerge put the halves back together. Grouping by both would
        // report a midnight-crossing session as two short ones.
        sql.Should().Contain("GROUP BY session_id");
        sql.Should().NotContain("GROUP BY session_id, event_date");
        sql.Should().Contain("minMerge(started_at)");
        sql.Should().Contain("maxMerge(ended_at)");
        sql.Should().Contain("countMerge(events)");
    }

    [Fact]
    public void TheRollupStatementReportsRangeScopedTotalsLikeTheRawOne()
    {
        (string sql, _) = GetSessionAnalyticsQuery.BuildQuery(Request(Midnight, NextWeek), Registry().Declared[0]);

        // The defect that made the dashboard carry a ROW_CAP workaround: totals computed over the
        // page rather than the range. The rollup path must not reintroduce it.
        sql.Should().Contain("(SELECT count() FROM sessions) AS total_sessions");
        sql.Should().Contain("(SELECT sum(event_count) FROM sessions) AS total_events");
    }

    [Fact]
    public void TheRangeIsHalfOpenOnTheRollupPath()
    {
        (string sql, _) = GetSessionAnalyticsQuery.BuildQuery(Request(Midnight, NextWeek), Registry().Declared[0]);

        // Matching /breakdown's rollup path exactly. Alignment has already guaranteed both ends sit
        // on a day boundary, so every day this touches is fully inside the request.
        sql.Should().Contain("event_date >= toDate({fromTimestamp:DateTime64})");
        sql.Should().Contain("event_date < toDate({toTimestamp:DateTime64})");
    }

    [Fact]
    public void BuildQueryPicksTheStatementTheRoutingChose()
    {
        (string raw, _) = GetSessionAnalyticsQuery.BuildQuery(Request(Midnight, NextWeek), null);
        (string routed, _) = GetSessionAnalyticsQuery.BuildQuery(
            Request(Midnight, NextWeek), Registry().Declared[0]);

        raw.Should().Contain("FROM nealytics_core.global_events");
        routed.Should().Contain("FROM nealytics_core.rollup_sessions");
    }

    [Fact]
    public void TheResponseSaysWhichStoreAnswered()
    {
        // Both paths return the same numbers by construction, so nothing else in the response would
        // reveal that a chart quietly changed its data source.
        GetSessionAnalyticsQuery.Aggregate("p", "t", [], 0, 0, 0, 100).Source.Should().Be("raw");
        GetSessionAnalyticsQuery.Aggregate("p", "t", [], 0, 0, 0, 100, "rollup:sessions")
            .Source.Should().Be("rollup:sessions");
    }
}

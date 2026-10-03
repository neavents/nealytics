using FluentAssertions;
using Nealytics.Engine.Features.GetBreakdown;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// The generic breakdown, and specifically its injection boundary.
///
/// ClickHouse has no parameter form for an identifier, so a group-by column has to reach the SQL as
/// text. Everything here exists to prove the text is never the caller's.
/// </summary>
public class BreakdownTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static QueryColumns Columns(params string[] dimensions) =>
        new(new DimensionRegistry(new TelemetryEngineOptions
        {
            Dimensions = [.. dimensions.Select(name => new DimensionOptions { Name = name })],
        }));

    private static QueryColumns ColumnsWithRetired(string name) =>
        new(new DimensionRegistry(new TelemetryEngineOptions
        {
            Dimensions = [new DimensionOptions { Name = name, Retired = true }],
        }));

    private static MeasureRegistry NoMeasures()
    {
        TelemetryEngineOptions options = new();
        return new MeasureRegistry(options, new DimensionRegistry(options));
    }

    internal static MeasureRegistry Measures(params (string Name, string Type, string Aggregations)[] measures)
    {
        TelemetryEngineOptions options = new()
        {
            Measures =
            [
                .. measures.Select(m => new MeasureOptions
                {
                    Name = m.Name,
                    Type = m.Type,
                    Aggregations = m.Aggregations,
                }),
            ],
        };

        return new MeasureRegistry(options, new DimensionRegistry(options));
    }

    private static BreakdownRequestResult Create(
        string? groupBy,
        QueryColumns? columns = null,
        string? metric = null,
        IReadOnlyList<string>? filters = null,
        string? limit = null,
        string? orderBy = null,
        string? eventType = null,
        string? projectId = "proj",
        string? tenantId = "tenant",
        MeasureRegistry? measures = null,
        string? traffic = null,
        string? exact = null,
        string? mode = null)
        => BreakdownRequestFactory.Create(
            projectId, tenantId, metric, groupBy, eventType,
            filters ?? [], null, null, limit, orderBy, traffic, exact, mode,
            columns ?? Columns("widget_id", "shelf_id"),
            measures ?? NoMeasures(),
            maxLimit: 10_000, defaultRangeHours: 24, nowUtc: Now);

    // ─── The allowlist ───

    [Fact]
    public void ValidGroupBy_OnADeclaredDimension_IsAccepted()
    {
        BreakdownRequestResult result = Create("widget_id");

        result.Success.Should().BeTrue();
        result.Request.GroupByColumn.Should().Be("widget_id");
    }

    [Theory]
    [InlineData("event_type")]
    [InlineData("object_id")]
    [InlineData("device_class")]
    [InlineData("os")]
    [InlineData("browser")]
    [InlineData("country")]
    [InlineData("session_id")]
    public void ValidGroupBy_OnACoreColumn_IsAccepted(string column)
    {
        Create(column).Success.Should().BeTrue();
    }

    [Fact]
    public void UnknownGroupBy_IsRejectedWith400_NamingIt()
    {
        BreakdownRequestResult result = Create("nonsense_id");

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(400);
        result.ErrorMessage.Should().Contain("nonsense_id",
            "a rejection that does not say what was wrong sends the caller reading source");
        result.ErrorMessage.Should().Contain("widget_id", "and it should say what IS accepted");
    }

    [Fact]
    public void MissingGroupBy_IsRejected()
    {
        Create(null).Success.Should().BeFalse();
        Create("").Success.Should().BeFalse();
        Create("   ").Success.Should().BeFalse();
    }

    [Fact]
    public void RetiredDimension_IsNotOfferedByTheQueryApi()
    {
        BreakdownRequestResult result = Create("old_id", ColumnsWithRetired("old_id"));

        result.Success.Should().BeFalse("retiring means the query API stops offering it");
        result.ErrorStatusCode.Should().Be(400);
    }

    [Fact]
    public void EventIdAndMetadataJson_AreNotGroupable()
    {
        // Grouping by a unique id returns one row per event; grouping by a JSON blob is the exact
        // thing typed columns exist to avoid.
        Create("event_id").Success.Should().BeFalse();
        Create("metadata_json").Success.Should().BeFalse();
    }

    // ─── Injection ───

    [Theory]
    [InlineData("article_id; DROP TABLE nealytics_core.global_events")]
    [InlineData("widget_id; DROP TABLE nealytics_core.global_events")]
    [InlineData("widget_id) UNION ALL SELECT 1,1,1,1 --")]
    [InlineData("1=1")]
    [InlineData("*")]
    [InlineData("widget_id--")]
    [InlineData("`widget_id`")]
    public void InjectionAttemptInGroupBy_IsRejectedAndNeverExecuted(string attempt)
    {
        BreakdownRequestResult result = Create(attempt);

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(400);
        result.Request.GroupByColumn.Should().BeNullOrEmpty(
            "nothing resolved, so there is no column for a query builder to be handed");
    }

    [Fact]
    public void InjectionAttemptInAFilterName_IsRejected()
    {
        BreakdownRequestResult result = Create("widget_id", filters: ["country' OR '1'='1:TR"]);

        result.Success.Should().BeFalse();
        result.ErrorStatusCode.Should().Be(400);
    }

    [Fact]
    public void ResolvedColumn_IsTheAllowlistsOwnInstance_NotTheCallersString()
    {
        // The strongest available property: the bytes that reach the SQL builder were in this
        // process before the request existed. "We validated it" is weaker than "it is not the
        // caller's string".
        QueryColumns columns = Columns("widget_id");
        string callerSupplied = new(['w', 'i', 'd', 'g', 'e', 't', '_', 'i', 'd']);

        columns.TryResolve(callerSupplied, out string resolved).Should().BeTrue();

        resolved.Should().Be(callerSupplied);
        ReferenceEquals(resolved, callerSupplied).Should().BeFalse(
            "the resolved value must come from the allowlist, not be handed back");
    }

    [Fact]
    public void AFilterValueIsParameterised_SoItsContentCannotReachTheSql()
    {
        // The value is hostile on purpose. It must appear as a parameter and nowhere in the text.
        const string hostile = "TR' OR 1=1 --";
        BreakdownRequestResult parsed = Create("widget_id", filters: [$"country:{hostile}"]);

        parsed.Success.Should().BeTrue();

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) =
            GetBreakdownQuery.BuildQuery(parsed.Request);

        sql.Should().NotContain(hostile);
        sql.Should().Contain("{filter0:String}");
        parameters.Should().ContainSingle(p => Equals(p.Value, hostile));
    }

    // ─── The statement ───

    [Fact]
    public void BuildQuery_GroupsByTheResolvedColumn_AndCoalescesNullToEmpty()
    {
        BreakdownRequestResult parsed = Create("widget_id");
        (string sql, _) = GetBreakdownQuery.BuildQuery(parsed.Request);

        sql.Should().Contain("ifNull(toString(widget_id), '') AS key");
        sql.Should().Contain("GROUP BY key");
    }

    [Theory]
    [InlineData("events", "count()")]
    [InlineData("sessions", "uniqExact(session_id)")]
    [InlineData("users", "uniqExact(user_id)")]
    public void BuildQuery_UsesTheRightAggregateForEachMetric(string metric, string aggregate)
    {
        BreakdownRequestResult parsed = Create("widget_id", metric: metric);
        (string sql, _) = GetBreakdownQuery.BuildQuery(parsed.Request);

        sql.Should().Contain(aggregate);
    }

    [Fact]
    public void BuildQuery_ForUsers_ExcludesAnonymousRows()
    {
        // Anonymous events carry a NULL user_id. Counting them distinct reports one phantom user
        // per group, which reads as real traffic.
        BreakdownRequestResult parsed = Create("widget_id", metric: "users");
        (string sql, _) = GetBreakdownQuery.BuildQuery(parsed.Request);

        sql.Should().Contain("user_id IS NOT NULL");
    }

    [Fact]
    public void BuildQuery_ForEvents_DoesNotExcludeAnonymousRows()
    {
        BreakdownRequestResult parsed = Create("widget_id", metric: "events");
        (string sql, _) = GetBreakdownQuery.BuildQuery(parsed.Request);

        sql.Should().NotContain("user_id IS NOT NULL",
            "an event count must include anonymous traffic — that is most of it");
    }

    [Fact]
    public void BuildQuery_AlwaysScopesToProjectAndTenant()
    {
        // Missing this is a cross-tenant read, not a wrong number.
        BreakdownRequestResult parsed = Create("widget_id");
        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) =
            GetBreakdownQuery.BuildQuery(parsed.Request);

        sql.Should().Contain("project_id = {projectId:String}");
        sql.Should().Contain("tenant_id = {tenantId:String}");
        parameters.Should().Contain(p => p.Key == "tenantId" && Equals(p.Value, "tenant"));
    }

    [Fact]
    public void BuildQuery_ReturnsTotalAndGroupCountInOneStatement()
    {
        // Three statements would each see a different set of rows as ingestion continues, so
        // `share` would not sum to what `total` implies and `truncated` could be computed against
        // a group count the rows never had.
        BreakdownRequestResult parsed = Create("widget_id");
        (string sql, _) = GetBreakdownQuery.BuildQuery(parsed.Request);

        sql.Should().Contain("WITH grouped AS");
        sql.Should().Contain("AS grand_total");
        sql.Should().Contain("AS group_count");
    }

    [Theory]
    [InlineData(null, "value DESC")]
    [InlineData("value_desc", "value DESC")]
    [InlineData("value_asc", "value ASC")]
    [InlineData("key_asc", "key ASC")]
    public void BuildQuery_HonoursOrderBy(string? orderBy, string expected)
    {
        BreakdownRequestResult parsed = Create("widget_id", orderBy: orderBy);
        (string sql, _) = GetBreakdownQuery.BuildQuery(parsed.Request);

        sql.Should().Contain("ORDER BY " + expected);
    }

    [Fact]
    public void UnknownOrderBy_IsRejected()
    {
        Create("widget_id", orderBy: "value_sideways").Success.Should().BeFalse();
    }

    [Fact]
    public void UnknownMetric_IsRejected()
    {
        BreakdownRequestResult result = Create("widget_id", metric: "revenue");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("revenue");
    }

    // ─── Caps ───

    [Fact]
    public void LimitIsClampedToMaxQueryLimit_RatherThanRejected()
    {
        // groupBy=session_id over a busy range would try to return millions of rows.
        BreakdownRequestResult result = Create("session_id", limit: "999999");

        result.Success.Should().BeTrue();
        result.Request.Limit.Should().Be(10_000);
    }

    [Fact]
    public void LimitBelowOne_IsClampedUp()
    {
        Create("widget_id", limit: "0").Request.Limit.Should().Be(1);
        Create("widget_id", limit: "-5").Request.Limit.Should().Be(1);
    }

    [Fact]
    public void TooManyFilters_AreRejected()
    {
        string[] filters = [.. Enumerable.Range(0, BreakdownRequestFactory.MaxFilters + 1)
            .Select(_ => "country:TR")];

        Create("widget_id", filters: filters).Success.Should().BeFalse();
    }

    // ─── Filters ───

    [Fact]
    public void FilterSplitsOnTheFirstColonOnly_SoAValueMayContainOne()
    {
        BreakdownRequestResult result = Create("widget_id", filters: ["object_id:/article/12:30"]);

        result.Success.Should().BeTrue();
        result.Request.Filters.Should().ContainSingle();
        result.Request.Filters[0].Column.Should().Be("object_id");
        result.Request.Filters[0].Value.Should().Be("/article/12:30");
    }

    [Fact]
    public void FilterWithoutAColon_IsRejected()
    {
        Create("widget_id", filters: ["country"]).Success.Should().BeFalse();
    }

    [Fact]
    public void MultipleFiltersAllApply()
    {
        BreakdownRequestResult parsed = Create(
            "widget_id", filters: ["country:TR", "device_class:mobile"]);

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) =
            GetBreakdownQuery.BuildQuery(parsed.Request);

        sql.Should().Contain("{filter0:String}");
        sql.Should().Contain("{filter1:String}");
        parameters.Should().Contain(p => Equals(p.Value, "TR"));
        parameters.Should().Contain(p => Equals(p.Value, "mobile"));
    }

    [Fact]
    public void EventTypeFilter_IsParameterised()
    {
        BreakdownRequestResult parsed = Create("widget_id", eventType: "article_view");

        (string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters) =
            GetBreakdownQuery.BuildQuery(parsed.Request);

        sql.Should().Contain("event_type = {eventType:String}");
        sql.Should().NotContain("article_view");
        parameters.Should().Contain(p => p.Key == "eventType" && Equals(p.Value, "article_view"));
    }

    // ─── Auth ───

    [Fact]
    public void MissingProjectOrTenantClaim_Is403_NotABadRequest()
    {
        Create("widget_id", projectId: null).ErrorStatusCode.Should().Be(403);
        Create("widget_id", tenantId: null).ErrorStatusCode.Should().Be(403);
    }

    // ─── Domain-freedom ───

    [Fact]
    public void WithNothingDeclared_TheAllowlistHasNoDeploymentVocabulary()
    {
        QueryColumns columns = Columns();

        columns.Allowed.Should().NotContain("article_id");
        columns.Allowed.Should().NotContain("section_id");
        columns.Allowed.Should().NotContain("table_id");
        columns.Allowed.Should().Contain("event_type", "core columns are always groupable");
    }

    [Fact]
    public void ADeclaredDimensionIsOfferedByName_WhateverThatNameIs()
    {
        // The same code answers "top articles" here and "top authors" in a clone.
        QueryColumns columns = Columns("author_id", "post_id");

        columns.Allowed.Should().Contain("author_id");
        columns.Allowed.Should().Contain("post_id");
        columns.TryResolve("author_id", out _).Should().BeTrue();
    }
}

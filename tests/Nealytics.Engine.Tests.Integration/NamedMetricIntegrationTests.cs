using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Features.BatchProcessor;
using Nealytics.Engine.Features.GetNamedMetric;
using Nealytics.Engine.Features.GetSavedReport;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Integration;

[Collection("ClickHouse")]
public sealed class NamedMetricIntegrationTests : IAsyncLifetime
{
    private readonly string _project = "c1_" + Guid.NewGuid().ToString("N");
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private Factory _factory = null!;
    private HttpClient _client = null!;
    private string Amount => "c1_amount_" + _suffix;
    private string Units => "c1_units_" + _suffix;
    private string Floating => "c1_float_" + _suffix;
    private string DecimalAmount => "c1_decimal_" + _suffix;

    public async Task InitializeAsync()
    {
        await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.global_events"
            + $" ADD COLUMN {Amount} Nullable(Int64), ADD COLUMN {Units} Nullable(String),"
            + $" ADD COLUMN {Floating} Nullable(Float64), ADD COLUMN {DecimalAmount} Nullable(Decimal(18,4))");
        await ClickHouseTestSupport.ExecuteAsync(TenantAttributeRegistry.BuildTableDdl(""));
        await ClickHouseTestSupport.ExecuteAsync($"INSERT INTO nealytics_core.tenant_attributes VALUES"
            + $" ('{_project}','cohort','t','group',now64(3)), ('{_project}','cohort','other','away',now64(3)),"
            + $" ('{_project}_other','cohort','other','group',now64(3))");
        string Row(string project, string tenant, string type, string traffic, string time, string amount, string unit, string number, string fraction) =>
            $"(generateUUIDv4(),'{project}','{tenant}','s','{type}','{traffic}','{time}',{amount},'{unit}',{number},{fraction})";
        string maximum = "9223372036854775807";
        string huge = "1.7976931348623157e308";
        string[] rows =
        [
            Row(_project, "t", "action", "normal", "2090-01-01 00:00:00.000", maximum, "u", huge, "0.1001"),
            Row(_project, "t", "action", "normal", "2090-01-01 00:00:01.999", maximum, "u", huge, "0.2002"),
            Row(_project, "t", "action", "normal", "2090-01-01 00:00:02.000", "5", "u", "0", "5"),
            Row(_project, "t", "action", "normal", "2089-12-31 23:59:59.999", "6", "u", "0", "6"),
            Row(_project, "t", "action", "normal", "2090-01-01 00:00:01.000", "7", "v", "NULL", "0.7003"),
            Row(_project, "t", "action", "normal", "2090-01-01 00:00:01.000", "NULL", "u", "NULL", "NULL"),
            Row(_project, "t", "action", "bot", "2090-01-01 00:00:01.000", "9", "u", "0", "9"),
            Row(_project, "t", "different", "normal", "2090-01-01 00:00:01.000", "10", "u", "0", "10"),
            Row(_project, "other", "action", "normal", "2090-01-01 00:00:01.000", "11", "u", "0", "11"),
            Row(_project + "_other", "t", "action", "normal", "2090-01-01 00:00:01.000", "12", "u", "0", "12"),
            Row(_project, "t", "action", "normal", "2090-01-01 01:00:00.000", "20", "u", "0", "2"),
            Row(_project, "t", "action", "normal", "2090-01-02 00:00:00.000", "30", "u", "0", "3")
        ];
        await ClickHouseTestSupport.ExecuteAsync("INSERT INTO nealytics_core.global_events"
            + $" (event_id,project_id,tenant_id,session_id,event_type,traffic_class,timestamp,{Amount},{Units},{Floating},{DecimalAmount}) VALUES "
            + string.Join(',', rows));
        await ClickHouseTestSupport.ExecuteAsync("INSERT INTO nealytics_core.global_events"
            + " (event_id,project_id,tenant_id,session_id,event_type,traffic_class,timestamp) VALUES"
            + $" (generateUUIDv4(),'{_project}','t','s','group_null','normal','2090-01-01 00:00:03.000')");
        var options = new TelemetryEngineOptions
        {
            Dimensions = [new() { Name = Units }],
            Measures =
            [
                new() { Name = Amount, Type = "Int64", UnitDimension = Units },
                new() { Name = Floating, Type = "Float64" },
                new() { Name = DecimalAmount, Type = "Decimal" }
            ],
            TenantAttributes = [new() { Name = "cohort" }],
            NamedMetrics = [new() { Name = "events", EventType = "action", Unit = "records" }]
        };
        foreach (string aggregation in new[] { "count", "sum", "avg", "min", "max" })
        {
            options.NamedMetrics.Add(new() { Name = aggregation + "_amount", Measure = Amount, Aggregation = aggregation, EventType = "action" });
        }
        options.NamedMetrics.Add(new() { Name = "sum_float", Measure = Floating, Aggregation = "sum", EventType = "action" });
        options.NamedMetrics.Add(new() { Name = "sum_decimal", Measure = DecimalAmount, Aggregation = "sum", EventType = "action" });
        options.EventSegments =
        [
            new() { Name = "selected", Filters = [Units + ":u", Amount + ">8"] },
            new() { Name = "other_tenant", Filters = ["tenant_id:other"] },
            new() { Name = "different_type", Filters = ["event_type:different"] },
            new() { Name = "literal", Filters = [Units + ":u' OR 1=1 --"] }
        ];
        foreach (var (name, operation) in new[] { ("eq", "="), ("ne", "!="), ("lt", "<"), ("lte", "<="), ("gt", ">"), ("gte", ">=") })
        {
            options.EventSegments.Add(new() { Name = name, Filters = [Amount + operation + "7"] });
        }
        options.NamedMetrics.Add(new() { Name = "all_events", Unit = "records" });
        options.NamedMetrics.Add(new() { Name = "different_events", EventType = "different" });
        options.NamedMetrics.Add(new() { Name = "empty_events", EventType = "absent" });
        options.NamedMetrics.Add(new() { Name = "empty_sum", Measure = Amount, Aggregation = "sum", EventType = "absent" });
        options.NamedMetrics.Add(new() { Name = "empty_avg", Measure = Amount, Aggregation = "avg", EventType = "absent" });
        options.NamedMetrics.Add(new() { Name = "untyped_sum", Measure = Amount, Aggregation = "sum" });
        string[] reportMetrics = ["events", "count_amount", "sum_amount", "avg_amount", "min_amount", "max_amount",
            "sum_float", "sum_decimal", "all_events", "different_events", "empty_events", "empty_sum", "empty_avg",
            "events", "untyped_sum", "avg_amount"];
        foreach (var (name, segment) in new[] { ("overview", ""), ("selected_report", "selected"),
            ("empty_report", "literal"), ("different_report", "different_type"), ("other_report", "other_tenant") })
        {
            options.SavedReports.Add(new()
            {
                Name = name, Segment = segment,
                Fields = reportMetrics.Select((metric, index) => new SavedReportFieldOptions { Name = "f" + index, Metric = metric }).ToList()
            });
        }
        MetricExpressionOptions Constant(string value) => new() { Operator = "constant", Value = value };
        MetricExpressionOptions Reference(string name) => new() { Operator = "metric", Metric = name };
        MetricExpressionOptions Operation(string operation, params MetricExpressionOptions[] arguments) =>
            new() { Operator = operation, Arguments = arguments.ToList() };
        options.NamedMetrics.Add(new()
        {
            Name = "share", Aggregation = "formula", Unit = "percent",
            Expression = Operation("multiply", Operation("divide", Reference("events"), Reference("all_events")), Constant("100"))
        });
        options.NamedMetrics.Add(new()
        {
            Name = "scaled", Aggregation = "formula",
            Expression = Operation("multiply", Reference("sum_amount"), Constant("1.0001"))
        });
        options.NamedMetrics.Add(new() { Name = "constant_value", Aggregation = "formula", Expression = Constant("9007199254740993.0001") });
        options.NamedMetrics.Add(new()
        {
            Name = "empty_ratio", Aggregation = "formula", Expression = Operation("divide", Reference("empty_events"), Reference("empty_events"))
        });
        options.NamedMetrics.Add(new()
        {
            Name = "nullable_formula", Aggregation = "formula", Expression = Operation("add", Reference("empty_avg"), Reference("empty_avg"))
        });
        options.NamedMetrics.Add(new()
        {
            Name = "float_formula", Aggregation = "formula", Expression = Operation("multiply", Reference("sum_float"), Constant("2"))
        });
        options.SavedReports.Add(new()
        {
            Name = "formula_report", Fields = new[] { "share", "scaled", "constant_value", "empty_ratio", "nullable_formula", "float_formula" }
                .Select(name => new SavedReportFieldOptions { Name = name, Metric = name }).ToList()
        });
        List<SavedReportFieldOptions> ShapedFields() => new[] { "events", "sum_amount", "share" }
            .Select(name => new SavedReportFieldOptions { Name = name, Metric = name }).ToList();
        options.SavedReports.Add(new()
        {
            Name = "grouped_report", GroupBy = [Units, "event_type"], Fields = ShapedFields()
        });
        options.SavedReports.Add(new()
        {
            Name = "time_report", GroupBy = [Units], Interval = "hour", Segment = "selected", Fields = ShapedFields()
        });
        options.SavedReports.Add(new()
        {
            Name = "daily_report", Interval = "day", Segment = "selected", Fields = ShapedFields()
        });
        options.SavedReports.Add(new()
        {
            Name = "limited_report", GroupBy = [Units], Limit = 1, Fields = [new() { Name = "events", Metric = "events" }]
        });
        options.SavedReports.Add(new()
        {
            Name = "parameter_report", Fields = [new() { Name = "total", Metric = "sum_amount" }, new() { Name = "events", Metric = "events" }],
            Parameters =
            [
                new() { Name = "unit", Column = Units, Default = "u" },
                new() { Name = "minimum", Column = Amount, Type = "number", Comparison = "gte", Default = "0" },
                new() { Name = "type", Column = "event_type", Required = true }
            ]
        });
        options.SavedReports.Add(new()
        {
            Name = "calendar_report", Interval = "day", Fields = [new() { Name = "constant", Metric = "constant_value" }]
        });
        options.SavedReports.Add(new()
        {
            Name = "nullable_report", GroupBy = [Units], Fields = [new() { Name = "events", Metric = "all_events" }]
        });
        _factory = new Factory(options);
        _client = _factory.CreateClient();
        _client.BaseAddress = new UriBuilder(_client.BaseAddress!) { Host = IPAddress.Loopback.ToString() }.Uri;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        await ClickHouseTestSupport.DeleteProjectsAsync(_project, _project + "_other");
        await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.tenant_attributes DELETE WHERE project_id IN ('{_project}','{_project}_other') SETTINGS mutations_sync=2");
        await ClickHouseTestSupport.ExecuteAsync($"ALTER TABLE nealytics_core.global_events"
            + $" DROP COLUMN {Amount}, DROP COLUMN {Units}, DROP COLUMN {Floating}, DROP COLUMN {DecimalAmount}");
    }

    [Theory]
    [InlineData("events", "", "4")]
    [InlineData("count_amount", "", "3")]
    [InlineData("sum_amount", "u", "18446744073709551614")]
    [InlineData("avg_amount", "v", "7")]
    [InlineData("min_amount", "u", "9223372036854775807")]
    [InlineData("max_amount", "u", "9223372036854775807")]
    [InlineData("sum_decimal", "", "1.0006")]
    [InlineData("sum_float", "", null)]
    public async Task PublishedHttpShapeReturnsScopedReferenceResults(string name, string unit, string? expected)
    {
        using var response = await GetAsync(name + (unit.Length == 0 ? "" : "&filter=" + Units + ":" + unit));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, document.RootElement.GetProperty("value").GetString());
        Assert.Equal(name, document.RootElement.GetProperty("name").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("raw", document.RootElement.GetProperty("source").GetString());
        Assert.Equal("string", document.RootElement.GetProperty("numericEncoding").GetString());
        Assert.Equal(new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc), document.RootElement.GetProperty("from").GetDateTime());
        Assert.Equal(new DateTime(2090, 1, 1, 0, 0, 2, DateTimeKind.Utc), document.RootElement.GetProperty("to").GetDateTime());
        if (name == "events")
        {
            Assert.Equal("records", document.RootElement.GetProperty("unit").GetString());
        }
    }

    [Theory]
    [InlineData("count_amount", "0")]
    [InlineData("events", "0")]
    [InlineData("sum_amount", "0")]
    [InlineData("sum_decimal", "0")]
    [InlineData("sum_float", "0")]
    [InlineData("avg_amount", null)]
    [InlineData("min_amount", null)]
    [InlineData("max_amount", null)]
    public async Task EmptySelectionsHaveDefinedZeroOrNullResults(string name, string? expected)
    {
        using var response = await GetAsync(name + "&filter=" + Units + ":missing");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, document.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task MeasureUnitRequirementsAndParameterValuesCannotBeBypassed()
    {
        using var unsafeUnits = await GetAsync("sum_amount");
        Assert.Equal(HttpStatusCode.BadRequest, unsafeUnits.StatusCode);
        using var injected = await GetAsync("events&filter=" + Units + ":" + Uri.EscapeDataString("u' OR 1=1 --"));
        Assert.Equal(HttpStatusCode.OK, injected.StatusCode);
        using var json = JsonDocument.Parse(await injected.Content.ReadAsStringAsync());
        Assert.Equal("0", json.RootElement.GetProperty("value").GetString());
        using var bots = await GetAsync("events&traffic=bot");
        using var botJson = JsonDocument.Parse(await bots.Content.ReadAsStringAsync());
        Assert.Equal("1", botJson.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task DiscoveryPreservesLegacyMetricsAndDoesNotExposeCompiledImplementationFields()
    {
        using var request = Authorized("/api/v1/schema", "t", "schema");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(document.RootElement.GetProperty("metrics").EnumerateArray(), m => m.GetString() == "events");
        var metrics = document.RootElement.GetProperty("namedMetrics");
        Assert.Equal(20, metrics.GetArrayLength());
        var sum = metrics.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "sum_amount");
        Assert.Equal(Units, sum.GetProperty("unitDimension").GetString());
        Assert.Equal(Amount, sum.GetProperty("measure").GetString());
        Assert.Equal("sum", sum.GetProperty("aggregation").GetString());
        Assert.False(sum.TryGetProperty("widenSum", out _));
    }

    [Fact]
    public async Task TenantSetMembershipAndNarrowingIntersectWithProjectScope()
    {
        using var members = await GetAsync("events", null);
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
        using var json = JsonDocument.Parse(await members.Content.ReadAsStringAsync());
        Assert.Equal("4", json.RootElement.GetProperty("value").GetString());
        using var narrowed = await GetAsync("events&tenant=other", null);
        using var narrowJson = JsonDocument.Parse(await narrowed.Content.ReadAsStringAsync());
        Assert.Equal("0", narrowJson.RootElement.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("1900-01-01T00:00:00Z", "1900-01-01T00:00:00.001Z")]
    [InlineData("2299-12-31T23:59:59.998Z", "2299-12-31T23:59:59.999Z")]
    public async Task CalendarEdgesExecuteWithRealDateTime64Parameters(string from, string to)
    {
        using var scope = _factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<GetNamedMetricQuery>();
        var scalar = await query.ExecuteAsync(new NamedMetricRequest(
            _factory.Services.GetRequiredService<NamedMetricRegistry>().Find("events")!, new QueryScope
            {
                ProjectId = _project, TenantId = "t",
                From = DateTime.Parse(from, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal),
                To = DateTime.Parse(to, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal),
                Filters = [], TrafficClass = "normal"
            }), CancellationToken.None);
        Assert.Equal("0", scalar.Value);
        using var request = Authorized("/api/v1/analytics/metrics/events?from=" + from + "&to=" + to, "t", "metrics");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("0", document.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task MillisecondRangesDoNotCollapseToSecondPrecision()
    {
        await ClickHouseTestSupport.ExecuteAsync("INSERT INTO nealytics_core.global_events"
            + " (event_id,project_id,tenant_id,session_id,event_type,timestamp) VALUES "
            + $"(generateUUIDv4(),'{_project}','t','s','action','2090-01-01 00:00:01.995')");
        using var request = Authorized("/api/v1/analytics/metrics/events"
            + "?from=2090-01-01T00:00:01.995Z&to=2090-01-01T00:00:01.999Z", "t", "metrics");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("1", document.RootElement.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("selected", "2")]
    [InlineData("eq", "1")]
    [InlineData("ne", "2")]
    [InlineData("lt", "0")]
    [InlineData("lte", "1")]
    [InlineData("gt", "2")]
    [InlineData("gte", "3")]
    [InlineData("other_tenant", "0")]
    [InlineData("different_type", "0")]
    [InlineData("literal", "0")]
    public async Task EventSegmentPredicatesExecuteAsScopedParameterizedIntersections(string segment, string expected)
    {
        using var response = await GetAsync("events&segment=" + segment);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, document.RootElement.GetProperty("value").GetString());
        Assert.Equal(segment, document.RootElement.GetProperty("segment").GetString());
    }

    [Fact]
    public async Task EventSegmentsSupportUnitSafetyAndRemainConstrainedByCallerFiltersAndMembership()
    {
        using var sum = await GetAsync("sum_amount&segment=selected");
        Assert.Equal(HttpStatusCode.OK, sum.StatusCode);
        using var sumJson = JsonDocument.Parse(await sum.Content.ReadAsStringAsync());
        Assert.Equal("18446744073709551614", sumJson.RootElement.GetProperty("value").GetString());
        using var contradictory = await GetAsync("events&segment=selected&filter=" + Units + ":v");
        using var contradictoryJson = JsonDocument.Parse(await contradictory.Content.ReadAsStringAsync());
        Assert.Equal("0", contradictoryJson.RootElement.GetProperty("value").GetString());
        using var members = await GetAsync("events&segment=other_tenant", null);
        using var membersJson = JsonDocument.Parse(await members.Content.ReadAsStringAsync());
        Assert.Equal("0", membersJson.RootElement.GetProperty("value").GetString());
        using var unknown = await GetAsync("events&segment=missing");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("\"Unknown event segment.\"", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EventSegmentDiscoveryPublishesOnlyDeclarativeFilters()
    {
        using var request = Authorized("/api/v1/schema", "t", "schema");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var declarations = document.RootElement.GetProperty("eventSegments");
        Assert.Equal(10, declarations.GetArrayLength());
        var selected = declarations.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "selected");
        Assert.Equal(1, selected.GetProperty("version").GetInt32());
        Assert.Equal(new[] { Units + ":u", Amount + ">8" }, selected.GetProperty("filters").EnumerateArray().Select(v => v.GetString()));
        Assert.False(selected.TryGetProperty("compiledFilters", out _));
    }

    [Theory]
    [InlineData("overview", "3", "4", "18446744073709551624")]
    [InlineData("selected_report", "2", "3", "18446744073709551624")]
    public async Task SavedReportReturnsOrderedExactValuesWithIndependentEventTypes(
        string name, string events, string allEvents, string untypedSum)
    {
        using var response = await ReportAsync(name, "&filter=" + Units + ":u");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(name, root.GetProperty("name").GetString());
        Assert.Equal("scalar", root.GetProperty("shape").GetString());
        Assert.Empty(root.GetProperty("rows").EnumerateArray());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("raw", root.GetProperty("source").GetString());
        Assert.Equal("string", root.GetProperty("numericEncoding").GetString());
        Assert.Equal(new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc), root.GetProperty("from").GetDateTime());
        Assert.Equal(new DateTime(2090, 1, 1, 0, 0, 2, DateTimeKind.Utc), root.GetProperty("to").GetDateTime());
        if (name == "selected_report")
        {
            Assert.Equal("selected", root.GetProperty("segment").GetString());
        }
        var fields = root.GetProperty("fields");
        Assert.Equal(16, fields.GetArrayLength());
        Assert.Equal(Enumerable.Range(0, 16).Select(index => "f" + index),
            fields.EnumerateArray().Select(field => field.GetProperty("name").GetString()));
        string? Value(int index) => fields[index].GetProperty("value").GetString();
        Assert.Equal(events, Value(0));
        Assert.Equal("events", fields[0].GetProperty("metric").GetString());
        Assert.Equal("records", fields[0].GetProperty("unit").GetString());
        Assert.Equal("2", Value(1));
        Assert.Equal("18446744073709551614", Value(2));
        Assert.Equal("9223372036854775807", Value(4));
        Assert.Equal("9223372036854775807", Value(5));
        Assert.Null(Value(6));
        Assert.Equal("0.3003", Value(7));
        Assert.Equal(allEvents, Value(8));
        Assert.Equal("1", Value(9));
        Assert.Equal("0", Value(10));
        Assert.Equal("0", Value(11));
        Assert.Null(Value(12));
        Assert.Equal(events, Value(13));
        Assert.Equal(untypedSum, Value(14));
        Assert.Equal(Value(3), Value(15));
        Assert.False(fields[0].TryGetProperty("definition", out _));
    }

    [Theory]
    [InlineData("empty_report", "")]
    [InlineData("overview", "&filter=country:absent")]
    [InlineData("other_report", "")]
    public async Task EmptyReportsReturnZeroCountsAndSumsAndExplicitNullExtrema(string name, string extra)
    {
        using var response = await ReportAsync(name, extra + "&filter=" + Units + ":u");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fields = document.RootElement.GetProperty("fields");
        foreach (int index in new[] { 0, 1, 2, 6, 7, 8, 9, 10, 11, 13, 14 })
        {
            Assert.Equal("0", fields[index].GetProperty("value").GetString());
        }
        foreach (int index in new[] { 3, 4, 5, 12, 15 })
        {
            Assert.Equal(JsonValueKind.Null, fields[index].GetProperty("value").ValueKind);
        }
    }

    [Fact]
    public async Task SharedSegmentsAndCallerFiltersIntersectAllFieldsWithoutOverridingMetricTypes()
    {
        using var different = await ReportAsync("different_report", "&filter=" + Units + ":u");
        Assert.Equal(HttpStatusCode.OK, different.StatusCode);
        using var json = JsonDocument.Parse(await different.Content.ReadAsStringAsync());
        var fields = json.RootElement.GetProperty("fields");
        Assert.Equal("0", fields[0].GetProperty("value").GetString());
        Assert.Equal("1", fields[8].GetProperty("value").GetString());
        Assert.Equal("1", fields[9].GetProperty("value").GetString());
        Assert.Equal("10", fields[14].GetProperty("value").GetString());
        using var selected = await ReportAsync("selected_report", "&filter=" + Units + ":v");
        using var selectedJson = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
        Assert.Equal("0", selectedJson.RootElement.GetProperty("fields")[0].GetProperty("value").GetString());
        using var average = await ReportAsync("overview", "&filter=" + Units + ":v");
        using var averageJson = JsonDocument.Parse(await average.Content.ReadAsStringAsync());
        Assert.Equal("7", averageJson.RootElement.GetProperty("fields")[3].GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("t", "", "3")]
    [InlineData("other", "", "1")]
    [InlineData(null, "", "3")]
    [InlineData(null, "&tenant=other", "0")]
    [InlineData("t", "&traffic=bot", "1")]
    [InlineData("t", "&traffic=all", "4")]
    public async Task ReportsPreserveTenantMembershipAndTrafficRestrictions(string? tenant, string extra, string expected)
    {
        using var response = await ReportAsync("overview", extra + "&filter=" + Units + ":u", tenant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, document.RootElement.GetProperty("fields")[0].GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("", 400)]
    [InlineData("&segment=selected&filter=", 400)]
    [InlineData("&unknown=x&filter=", 400)]
    public async Task ReportValidationRejectsUnitAmbiguityAndUnsupportedParameters(string extra, int expected)
    {
        using var response = await ReportAsync("overview", extra.Length == 0 ? "" : extra + Units + ":u");
        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("1900-01-01T00:00:00Z", "1900-01-01T00:00:00.001Z")]
    [InlineData("2299-12-31T23:59:59.998Z", "2299-12-31T23:59:59.999Z")]
    public async Task ReportCalendarEdgesUseDateTime64AndReturnEmptyAggregates(string from, string to)
    {
        using var request = Authorized("/api/v1/analytics/reports/selected_report?from=" + from + "&to=" + to, "t", "reports");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("0", document.RootElement.GetProperty("fields")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task SchemaPublishesCanonicalReportsAndNeverCompiledFields()
    {
        using var request = Authorized("/api/v1/schema", "t", "schema");
        using var response = await _client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var reports = document.RootElement.GetProperty("savedReports");
        Assert.Equal(13, reports.GetArrayLength());
        var report = reports.EnumerateArray().Single(item => item.GetProperty("name").GetString() == "selected_report");
        Assert.Equal(1, report.GetProperty("version").GetInt32());
        Assert.Equal("selected", report.GetProperty("segment").GetString());
        var first = report.GetProperty("fields")[0];
        Assert.Equal("f0", first.GetProperty("name").GetString());
        Assert.Equal("events", first.GetProperty("metric").GetString());
        Assert.False(first.TryGetProperty("definition", out _));
    }

    [Theory]
    [InlineData("share", "", "80")]
    [InlineData("share", "&filter=", "75")]
    [InlineData("scaled", "&filter=", "18448588748116922569.1614")]
    [InlineData("constant_value", "", "9007199254740993.0001")]
    [InlineData("empty_ratio", "", null)]
    [InlineData("nullable_formula", "&filter=", null)]
    [InlineData("float_formula", "", null)]
    public async Task FormulaMetricsFinalizeExactComponentsAndPreserveNullSemantics(string name, string suffix, string? expected)
    {
        using var response = await GetAsync(name + (suffix.Length == 0 ? "" : suffix + Units + ":u"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, document.RootElement.GetProperty("value").GetString());
        if (name == "share")
        {
            Assert.Equal("percent", document.RootElement.GetProperty("unit").GetString());
        }
    }

    [Fact]
    public async Task FormulaReportsCombinePrimitiveAndComputedValuesInOneScopedProjection()
    {
        using var response = await ReportAsync("formula_report", "&filter=" + Units + ":u");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new string?[] { "75", "18448588748116922569.1614", "9007199254740993.0001", null, null, null },
            document.RootElement.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("value").GetString()));
        using var invalid = await ReportAsync("formula_report");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var selected = await GetAsync("share&segment=selected");
        using var selectedJson = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
        Assert.Equal("66.6666666666666666666666666666666667", selectedJson.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task DeclaredGroupingsPreserveIndependentMetricTypesAndUnitBoundaries()
    {
        using var response = await ReportAsync("grouped_report");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("table", document.RootElement.GetProperty("shape").GetString());
        Assert.Empty(document.RootElement.GetProperty("fields").EnumerateArray());
        var rows = document.RootElement.GetProperty("rows");
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal(new[] { "u", "u", "v" }, rows.EnumerateArray().Select(row => row.GetProperty("groups")[0].GetProperty("value").GetString()));
        Assert.Equal(new[] { "action", "different", "action" }, rows.EnumerateArray().Select(row => row.GetProperty("groups")[1].GetProperty("value").GetString()));
        Assert.Equal(new[] { "3", "0", "1" }, rows.EnumerateArray().Select(row => row.GetProperty("fields")[0].GetProperty("value").GetString()));
        Assert.Equal(new[] { "18446744073709551614", "0", "7" }, rows.EnumerateArray().Select(row => row.GetProperty("fields")[1].GetProperty("value").GetString()));
        Assert.Equal(new[] { "100", "0", "100" }, rows.EnumerateArray().Select(row => row.GetProperty("fields")[2].GetProperty("value").GetString()));
        Assert.Equal(Units, rows[0].GetProperty("groups")[0].GetProperty("name").GetString());
        Assert.Equal("event_type", rows[0].GetProperty("groups")[1].GetProperty("name").GetString());
        using var limited = await ReportAsync("limited_report");
        using var limitedJson = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Single(limitedJson.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal("u", limitedJson.RootElement.GetProperty("rows")[0].GetProperty("groups")[0].GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("time_report", 3, "2", "66.6666666666666666666666666666666667")]
    [InlineData("daily_report", 2, "3", "75")]
    public async Task TimeSeriesAggregateEachBucketBeforeFinalizingFormulas(string name, int expectedRows, string firstCount, string firstShare)
    {
        using var response = await ShapedAsync(name, "2090-01-01T00:00:00Z", "2090-01-03T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("timeseries", document.RootElement.GetProperty("shape").GetString());
        var rows = document.RootElement.GetProperty("rows");
        Assert.Equal(expectedRows, rows.GetArrayLength());
        Assert.Equal(new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc), rows[0].GetProperty("bucket").GetDateTime());
        Assert.Equal(DateTimeKind.Utc, rows[0].GetProperty("bucket").GetDateTime().Kind);
        Assert.Equal(firstCount, rows[0].GetProperty("fields")[0].GetProperty("value").GetString());
        Assert.Equal(firstShare, rows[0].GetProperty("fields")[2].GetProperty("value").GetString());
        Assert.Equal("1", rows[expectedRows - 1].GetProperty("fields")[0].GetProperty("value").GetString());
        Assert.Equal("100", rows[expectedRows - 1].GetProperty("fields")[2].GetProperty("value").GetString());
        Assert.Equal(new DateTime(2090, 1, 2, 0, 0, 0, DateTimeKind.Utc), rows[expectedRows - 1].GetProperty("bucket").GetDateTime());
    }

    [Fact]
    public async Task TimeSeriesRetainMillisecondExclusiveBoundsAndSparseEmptySelections()
    {
        using var response = await ShapedAsync("time_report", "2090-01-01T00:00:00.001Z", "2090-01-02T00:00:00.001Z");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = document.RootElement.GetProperty("rows");
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal("1", rows[0].GetProperty("fields")[0].GetProperty("value").GetString());
        Assert.Equal("50", rows[0].GetProperty("fields")[2].GetProperty("value").GetString());
        using var empty = await ReportAsync("time_report", "&filter=country:absent");
        using var emptyJson = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
        Assert.Empty(emptyJson.RootElement.GetProperty("rows").EnumerateArray());
    }

    [Theory]
    [InlineData("&param.type=action", "18446744073709551614", "2")]
    [InlineData("&param.type=action&param.unit=v", "7", "1")]
    [InlineData("&param.type=action&param.unit=v&param.minimum=8", "0", "0")]
    [InlineData("&param.type=different", "0", "0")]
    [InlineData("&param.type=action&param.unit=u%27%20OR%201%3D1", "0", "0")]
    public async Task DeclaredParametersBindTypedValuesAndParticipateInUnitSafety(string extra, string sum, string count)
    {
        using var response = await ReportAsync("parameter_report", extra);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(sum, document.RootElement.GetProperty("fields")[0].GetProperty("value").GetString());
        Assert.Equal(count, document.RootElement.GetProperty("fields")[1].GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("&param.type=action&param.type=other")]
    [InlineData("&param.type=action&param.minimum=nan")]
    [InlineData("&param.type=action&param.missing=x")]
    [InlineData("&param.type=action&groupBy=country")]
    public async Task InvalidReportParametersAndShapeOverridesAreRejected(string extra)
    {
        using var response = await ReportAsync("parameter_report", extra);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("1900-01-01T00:00:00Z", "1900-01-01T00:00:00.001Z", "1900-01-01T00:00:00Z")]
    [InlineData("2299-12-31T23:59:59.998Z", "2299-12-31T23:59:59.999Z", "2299-12-31T00:00:00Z")]
    public async Task TimeBucketsKeepTheFullDateTime64Calendar(string from, string to, string expected)
    {
        using var response = await ShapedAsync("calendar_report", from, to);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(document.RootElement.GetProperty("rows").EnumerateArray());
        using var scope = _factory.Services.CreateScope();
        var definition = _factory.Services.GetRequiredService<SavedReportRegistry>().Find("calendar_report")!;
        var request = new SavedReportRequest(definition, new QueryScope
        {
            ProjectId = _project, TenantId = "t", TrafficClass = "normal", Filters = [],
            From = DateTime.Parse(from, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal),
            To = DateTime.Parse(to, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal)
        });
        var query = GetSavedReportQuery.BuildQuery(request);
        string source = "(SELECT {projectId:String} AS project_id, {tenantId:String} AS tenant_id, 'normal' AS traffic_class,"
            + " toDateTime64({at:String}, 3, 'UTC') AS timestamp)";
        string sql = query.Sql.Replace("nealytics_core.global_events", source, StringComparison.Ordinal);
        var parameters = query.Parameters.Append(new KeyValuePair<string, object?>("at", request.Scope.From.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var reader = new MetricAggregateReader(scope.ServiceProvider.GetRequiredService<ClickHouseConnectionFactory>());
        var row = Assert.Single(await reader.ReadRowsAsync(sql, parameters, [], 0, true, 1, CancellationToken.None));
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal), row.Bucket);
        Assert.Equal("9007199254740993.0001", MetricProjection.Evaluate(definition.Fields[0].Definition, row.Values));
    }

    [Fact]
    public async Task GroupNullValuesRemainExplicitAndDistinctFromEmptyStrings()
    {
        using var response = await ShapedAsync("nullable_report", "2090-01-01T00:00:00Z", "2090-01-01T00:00:04Z",
            "&filter=event_type:group_null");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(document.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("groups")[0].GetProperty("value").ValueKind);
        Assert.Equal("1", row.GetProperty("fields")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task AggregateReaderHonorsItsOwnRowBoundAndCancellation()
    {
        using var scope = _factory.Services.CreateScope();
        var reader = new MetricAggregateReader(scope.ServiceProvider.GetRequiredService<ClickHouseConnectionFactory>());
        var metric = _factory.Services.GetRequiredService<NamedMetricRegistry>().Find("events")!;
        var rows = await reader.ReadRowsAsync("SELECT toString(number) FROM numbers(2)", [], [metric], 0, false, 1, CancellationToken.None);
        Assert.Single(rows);
        Assert.Equal("0", rows[0].Values["events"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadRowsAsync(
            "SELECT '0'", [], [metric], 0, false, 1, cancellation.Token));
        rows = await reader.ReadRowsAsync("SELECT '1'", [], [metric], 0, false, 1, CancellationToken.None);
        Assert.Equal("1", Assert.Single(rows).Values["events"]);
    }

    private async Task<HttpResponseMessage> ShapedAsync(string name, string from, string to, string extra = "")
    {
        using var request = Authorized("/api/v1/analytics/reports/" + name + "?from=" + from + "&to=" + to + extra, "t", "reports");
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> ReportAsync(string name, string extra = "", string? tenant = "t")
    {
        using var request = Authorized("/api/v1/analytics/reports/" + name
            + "?from=2090-01-01T00:00:00Z&to=2090-01-01T00:00:02Z" + extra, tenant, "reports");
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetAsync(string name, string? tenant = "t")
    {
        int split = name.IndexOf('&');
        string metric = split < 0 ? name : name[..split];
        string extra = split < 0 ? "" : name[split..];
        using var request = Authorized("/api/v1/analytics/metrics/" + metric
            + "?from=2090-01-01T00:00:00Z&to=2090-01-01T00:00:02Z" + extra, tenant, "metrics");
        return await _client.SendAsync(request);
    }

    private HttpRequestMessage Authorized(string path, string? tenant, string scope)
    {
        List<Claim> claims = [new("project_id", _project), new("scope", scope)];
        claims.Add(tenant is null ? new("tenant_set", "cohort:group") : new("tenant_id", tenant));
        var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes("this-is-a-test-jwt-key-at-least-32-bytes-long!!")), SecurityAlgorithms.HmacSha256));
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }

    private sealed class Factory : TestWebApplicationFactory
    {
        private readonly TelemetryEngineOptions _declared;
        private readonly string _directory;

        public Factory(TelemetryEngineOptions declared)
        {
            _declared = declared;
            _directory = Environment.GetEnvironmentVariable("TelemetryEngine__WriteAheadLogDirectory")!;
            UseKestrel(0);
            ClientOptions.AllowAutoRedirect = false;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var dimensions = new DimensionRegistry(_declared);
                var measures = new MeasureRegistry(_declared, dimensions);
                var attributes = new TenantAttributeRegistry(_declared);
                services.AddSingleton(dimensions);
                services.AddSingleton(measures);
                services.AddSingleton(attributes);
                services.AddSingleton(new QueryColumns(dimensions, attributes));
                var metrics = new NamedMetricRegistry(_declared, measures);
                var segments = new EventSegmentRegistry(_declared, new QueryColumns(dimensions, attributes), measures);
                services.AddSingleton(metrics);
                services.AddSingleton(segments);
                services.AddSingleton(new SavedReportRegistry(_declared, metrics, segments));
                services.PostConfigure<TelemetryEngineOptions>(options => options.ReadScopeClaim = "scope");
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)
                    && (d.ImplementationType == typeof(ClickHouseSchemaMigrator)
                        || d.ImplementationType == typeof(TelemetryBatchProcessor))).ToArray())
                {
                    services.Remove(descriptor);
                }
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Features.GetProjectTimeline;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Query;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

public class QueryGuardTests : IClassFixture<NoDatabaseWebFactory>
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly HttpClient _client;
    private readonly IServiceProvider _services;

    public QueryGuardTests(NoDatabaseWebFactory factory)
    {
        _client = factory.CreateClient();
        _services = factory.Services;
    }

    private static QueryGuard Guard(int days = 92, int seconds = 30) =>
        new(new TelemetryEngineOptions { MaxQueryRangeDays = days, QueryExecutionTimeoutSeconds = seconds });

    private static string Jwt()
    {
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(NoDatabaseWebFactory.JwtKey));
        JwtSecurityToken token = new(
            claims: [new Claim("project_id", "p"), new Claim("tenant_id", "t")],
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public void DefaultsAreNinetyTwoDaysAndThirtySeconds()
    {
        TelemetryEngineOptions options = new();

        options.MaxQueryRangeDays.Should().Be(92);
        options.QueryExecutionTimeoutSeconds.Should().Be(30);
    }

    [Fact]
    public void LimitAppendsTheExecutionSettingsExactly()
    {
        Guard(seconds: 7).Limit("SELECT 1 LIMIT {limit:Int32}").Should().Be(
            "SELECT 1 LIMIT {limit:Int32} SETTINGS max_execution_time = 7, timeout_overflow_mode = 'throw'");
    }

    [Fact]
    public void ARangeOfExactlyTheMaximumIsAdmittedAndOneTickMoreIsNot()
    {
        QueryGuard guard = Guard(days: 92);
        DateTime from = Now.AddDays(-92);

        guard.Admits(from, Now).Should().BeTrue();
        guard.Admits(from.AddTicks(-1), Now).Should().BeFalse();
        guard.Admits(Now, Now).Should().BeTrue();
    }

    [Fact]
    public void FloorIsTheUpperBoundMinusTheMaximum()
    {
        Guard(days: 10).Floor(Now).Should().Be(Now.AddDays(-10));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(92, 0)]
    [InlineData(92, -5)]
    public void ANonPositiveLimitRefusesTheBoot(int days, int seconds)
    {
        Action build = () => Guard(days, seconds);

        build.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TheRangeDetailNamesTheSpanAndTheLimit()
    {
        string detail = Guard(days: 92).RangeDetail(Now.AddDays(-100), Now);

        detail.Should().Contain("100 days").And.Contain("At most 92 days");
    }

    [Fact]
    public void ANonClickHouseExceptionIsNotATimeout()
    {
        ClickHouseTimeoutFault.IsTimeout(new InvalidOperationException("timeout")).Should().BeFalse();
        ClickHouseTimeoutFault.IsTimeout(new TimeoutException()).Should().BeFalse();
    }

    [Fact]
    public void TheTimelineIsBoundedBelowByTheMaximumRange()
    {
        TimelineRequestResult unbounded = TimelineRequestFactory.Create(
            "p", "t", null, null, null, null, null, null, null, 100, TimeSpan.FromDays(92), Now);
        TimelineRequestResult paged = TimelineRequestFactory.Create(
            "p", "t", null, "2026-09-01T00:00:00Z", null, null, null, null, null, 100, TimeSpan.FromDays(92), Now);

        unbounded.Request.NotBefore.Should().Be(Now.AddDays(-92));
        paged.Request.NotBefore.Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-92));
    }

    [Fact]
    public void TheTimelineCursorWithAnOffsetIsNormalisedToUtc()
    {
        TimelineRequestResult result = TimelineRequestFactory.Create(
            "p", "t", null, "2026-09-01T03:00:00+03:00", null, null, null, null, null, 100, TimeSpan.FromDays(1), Now);

        result.Request.Before.Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        result.Request.Before!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void TheGuardIsRegisteredFromTheDeploymentOptions()
    {
        QueryGuard guard = _services.GetRequiredService<QueryGuard>();

        guard.MaxRangeDays.Should().Be(92);
        guard.ExecutionTimeoutSeconds.Should().Be(30);
    }

    public static TheoryData<string> RangedRoutes() =>
    [
        "/api/v1/analytics/sessions?limit=5",
        "/api/v1/analytics/timeseries?interval=day",
        "/api/v1/analytics/active?interval=day",
        "/api/v1/analytics/top?dimension=event_type",
        "/api/v1/analytics/breakdown?groupBy=event_type&metric=events",
        "/api/v1/analytics/funnel?step=a&step=b",
        "/api/v1/analytics/pivot?groupBy=event_type&metric=events",
        "/api/v1/analytics/distribution?of=session_duration",
    ];

    [Theory]
    [MemberData(nameof(RangedRoutes))]
    public async Task ARangeWiderThanTheMaximumIsA400ProblemBeforeAnyQuery(string route)
    {
        HttpRequestMessage request = new(
            HttpMethod.Get, route + "&from=2026-01-01T00:00:00Z&to=2026-06-01T00:00:00Z");
        request.Headers.Add("Authorization", $"Bearer {Jwt()}");

        HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().Should().Be(400);
        body.RootElement.GetProperty("title").GetString().Should().Be("Query range too wide");
        body.RootElement.GetProperty("detail").GetString().Should().Contain("At most 92 days");
    }

    [Fact]
    public async Task TheTimeoutProblemIsA422WithItsOwnTitle()
    {
        DefaultHttpContext context = new() { RequestServices = _services };
        context.Response.Body = new MemoryStream();

        await Guard(seconds: 5).RejectTimeout().ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(422);
        context.Response.Body.Position = 0;
        using JsonDocument body = await JsonDocument.ParseAsync(context.Response.Body);
        body.RootElement.GetProperty("title").GetString().Should().Be("Query exceeded its execution time");
        body.RootElement.GetProperty("detail").GetString().Should().Contain("5 second");
    }

    [Fact]
    public async Task TheMiddlewareLetsAnyOtherFailureThrough()
    {
        QueryTimeoutMiddleware middleware = new(_ => throw new InvalidOperationException("boom"), Guard());
        DefaultHttpContext context = new() { RequestServices = _services };

        Func<Task> act = () => middleware.InvokeAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

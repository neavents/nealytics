using Nealytics.Engine.Infrastructure.Query;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// Every read endpoint, reached over HTTP, with ClickHouse deliberately unreachable.
///
/// This exists because two endpoints shipped with a service that was never registered. The build
/// was clean, 602 unit tests were green, and both would have thrown on the first real request:
/// minimal APIs treat an unresolvable concrete parameter as a JSON body parameter, so the failure
/// is a startup/binding error, not a compile error. Nothing that tests a request factory or a SQL
/// builder can see it — only actually calling the route can.
///
/// A 500 from a dead database is fine here and expected. What must never happen is a 500 whose
/// cause is dependency injection.
/// </summary>
public class EveryEndpointResolvesTests : IClassFixture<NoDatabaseWebFactory>
{
    private readonly HttpClient _client;
    private readonly IServiceProvider _services;

    public EveryEndpointResolvesTests(NoDatabaseWebFactory factory)
    {
        _client = factory.CreateClient();
        _services = factory.Services;
    }

    private static string Jwt()
    {
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(NoDatabaseWebFactory.JwtKey));
        JwtSecurityToken token = new(
            claims: [new Claim("project_id", "p"), new Claim("tenant_id", "t")],
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static TheoryData<string> ReadRoutes() =>
    [
        "/api/v1/telemetry/timeline?limit=5",
        "/api/v1/analytics/sessions?limit=5",
        "/api/v1/analytics/timeseries?interval=hour",
        "/api/v1/analytics/active?interval=day",
        "/api/v1/analytics/top?dimension=event_type&limit=5",
        "/api/v1/analytics/breakdown?groupBy=event_type&metric=events",
        "/api/v1/analytics/funnel?step=a&step=b",
        "/api/v1/analytics/pivot?groupBy=event_type&metric=events&metric=sessions:view",
        "/api/v1/analytics/distribution?of=session_duration",
        "/api/v1/analytics/compare?metric=events&groupBy=event_type",
        "/api/v1/analytics/timeseries?interval=day&metric=sessions",
        "/api/v1/schema",
    ];

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task EveryReadRoute_ResolvesItsDependencies(string route)
    {
        HttpRequestMessage request = new(HttpMethod.Get, route);
        request.Headers.Add("Authorization", $"Bearer {Jwt()}");

        string evidence;

        try
        {
            HttpResponseMessage response = await _client.SendAsync(request);

            response.StatusCode.Should().NotBe(
                HttpStatusCode.NotFound, "the route must be mapped");

            evidence = await response.Content.ReadAsStringAsync();
        }
        catch (Exception exception)
        {
            // ClickHouse is deliberately unreachable in this fixture, so a socket failure means the
            // endpoint resolved everything it needed and got as far as querying. That is a pass.
            evidence = exception.ToString();
        }

        evidence.Should().NotContain("JsonTypeInfo metadata for type",
            "minimal APIs bind an unresolvable concrete parameter as a JSON body parameter instead "
            + "of failing the build, which is exactly how GetEventTypesQuery and GetFunnelQuery "
            + "shipped broken behind a clean build and a green suite");
        evidence.Should().NotContain("Unable to resolve service");
        evidence.Should().NotContain("No service for type");
    }

    /// <summary>
    /// Every type an endpoint takes as a parameter must be resolvable from the container.
    ///
    /// This is asserted directly rather than through a request because minimal APIs do not fail
    /// loudly on an unregistered parameter: they reclassify it as a JSON body parameter, bind null
    /// for a GET, and the handler then dereferences it. The symptom is a NullReferenceException at
    /// runtime that looks nothing like a wiring mistake.
    /// </summary>
    [Theory]
    [InlineData(typeof(Nealytics.Engine.Features.GetProjectTimeline.GetProjectTimelineQuery))]
    [InlineData(typeof(Nealytics.Engine.Features.GetSessionAnalytics.GetSessionAnalyticsQuery))]
    [InlineData(typeof(Nealytics.Engine.Features.GetEventTimeSeries.GetEventTimeSeriesQuery))]
    [InlineData(typeof(Nealytics.Engine.Features.GetActiveUsers.GetActiveUsersQuery))]
    [InlineData(typeof(Nealytics.Engine.Features.GetTopEvents.GetTopEventsQuery))]
    [InlineData(typeof(Nealytics.Engine.Features.GetBreakdown.GetBreakdownQuery))]
    [InlineData(typeof(Nealytics.Engine.Features.GetFunnel.GetFunnelQuery))]
    [InlineData(typeof(Nealytics.Engine.Features.GetSchema.GetEventTypesQuery))]
    [InlineData(typeof(Nealytics.Engine.Features.GetPivot.GetPivotQuery))]
    [InlineData(typeof(Nealytics.Engine.Features.GetDistribution.GetDistributionQuery))]
    [InlineData(typeof(Nealytics.Engine.Features.GetComparison.GetComparisonQuery))]
    [InlineData(typeof(Nealytics.Engine.Features.GetUnseenObjects.GetUnseenObjectsQuery))]
    [InlineData(typeof(Nealytics.Engine.Infrastructure.Query.QueryGuard))]
    [InlineData(typeof(Nealytics.Engine.Infrastructure.Query.QueryColumns))]
    [InlineData(typeof(Nealytics.Engine.Features.IngestTelemetry.DimensionSanitizer))]
    [InlineData(typeof(Nealytics.Engine.Features.IngestTelemetry.MeasureSanitizer))]
    [InlineData(typeof(Nealytics.Engine.Infrastructure.Configuration.DimensionRegistry))]
    [InlineData(typeof(Nealytics.Engine.Infrastructure.Configuration.MeasureRegistry))]
    [InlineData(typeof(Nealytics.Engine.Infrastructure.Configuration.RollupRegistry))]
    public void EveryEndpointDependency_IsRegistered(Type dependency)
    {
        using IServiceScope scope = _services.CreateScope();

        object? resolved = scope.ServiceProvider.GetService(dependency);

        resolved.Should().NotBeNull(
            "{0} is an endpoint parameter; unregistered, it is silently bound as a request body "
            + "instead of failing the build", dependency.Name);
    }

    [Fact]
    public async Task SchemaAnswersEvenWhenClickHouseIsDown()
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/schema");
        request.Headers.Add("Authorization", $"Bearer {Jwt()}");

        HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the declarative half of the schema needs no database, so a blip must not take it down");

        (await response.Content.ReadAsStringAsync()).Should().Contain("\"eventTypesAvailable\":false");
    }
}

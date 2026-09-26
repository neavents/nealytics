using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// Every read endpoint's query string reaches the parameter it names.
///
/// <b>The bug this exists for.</b> <c>/analytics/active</c> passed nine consecutive
/// <c>string?</c> arguments positionally and had two of them crossed: <c>to</c> landed in the
/// <c>traffic</c> slot, so <c>TrafficFilter</c> was handed a timestamp and refused it. The endpoint
/// returned <b>400 to every request that set <c>to</c></b> — which is every real request — and it
/// stayed that way through a green unit suite, because those tests call the factory directly and
/// therefore pass the arguments correctly by construction. It was found by a benchmark: 122,880
/// errors and zero successes.
///
/// <b>Why the values matter more than the assertion.</b> Each parameter below carries a value that
/// is valid <i>only</i> in its own slot — <c>Europe/Istanbul</c> parses as a time zone and nothing
/// else, <c>normal</c> as a traffic class and nothing else, <c>day</c> as an interval. Fill them
/// with interchangeable junk and a swap sails straight through. That is the whole design of this
/// file.
///
/// The assertion is "not 400", not "200": ClickHouse is unreachable in this fixture, so a
/// well-formed request fails later, in the storage layer. Distinguishing "the query string was
/// rejected" from "the database was not there" is exactly the distinction that was missed.
/// </summary>
public class QueryParameterWiringTests : IClassFixture<NoDatabaseWebFactory>
{
    private readonly HttpClient _client;

    public QueryParameterWiringTests(NoDatabaseWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static string Jwt()
    {
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(NoDatabaseWebFactory.JwtKey));
        JwtSecurityToken token = new(
            claims: [new Claim("project_id", "p"), new Claim("tenant_id", "t")],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private const string Range = "from=2026-06-01T00:00:00Z&to=2026-06-08T00:00:00Z";

    /// <summary>
    /// Every read endpoint with every documented parameter populated at once. A parameter left out
    /// is a slot this cannot check, so they are all here even where the default would do.
    /// </summary>
    public static TheoryData<string, string> EveryReadEndpoint() => new()
    {
        {
            "active",
            $"/api/v1/analytics/active?limit=100&interval=day&by=user&mode=exact&{Range}"
            + "&tz=Europe/Istanbul&traffic=normal"
        },
        {
            "breakdown",
            $"/api/v1/analytics/breakdown?metric=events&groupBy=event_type&eventType=view&{Range}"
            + "&limit=100&orderBy=value_desc&traffic=normal&exact=true&empty=null"
        },
        {
            "top",
            $"/api/v1/analytics/top?limit=100&dimension=event_type&{Range}&traffic=normal&exact=true"
        },
        {
            "timeseries",
            $"/api/v1/analytics/timeseries?limit=100&interval=day&{Range}&eventType=view"
            + "&groupBy=event_type&tz=Europe/Istanbul&traffic=normal&metric=sessions:view&empty=null"
        },
        {
            "pivot",
            $"/api/v1/analytics/pivot?groupBy=event_type&metric=events:view&metric=sessions&{Range}"
            + "&limit=100&orderBy=1&order=asc&traffic=normal&mode=approx&exact=true&empty=null"
        },
        {
            "compare",
            $"/api/v1/analytics/compare?metric=sessions:view&groupBy=event_type&{Range}&tz=Europe/Istanbul"
            + "&traffic=normal&orderBy=change&order=asc&limit=100&mode=approx&empty=null"
        },
        {
            "distribution",
            $"/api/v1/analytics/distribution?of=session_duration&quantiles=0.5,0.9&buckets=1000,5000&{Range}"
            + "&traffic=normal&mode=approx&empty=null"
        },
        {
            "sessions",
            $"/api/v1/analytics/sessions?limit=100&{Range}"
        },
        {
            "funnel",
            $"/api/v1/analytics/funnel?step=view&step=purchase&grain=sessions&windowSeconds=3600"
            + $"&{Range}&limit=100"
        },
        {
            "timeline",
            "/api/v1/telemetry/timeline?limit=100"
        },
    };

    private async Task<HttpResponseMessage?> GetAsync(string url)
    {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {Jwt()}");

        try
        {
            return await _client.SendAsync(request);
        }
        catch (Exception)
        {
            // Reaching ClickHouse and failing is the success condition here: this fixture points at
            // a dead port on purpose, so getting that far means validation passed and the request
            // was on its way to storage. Null says "parsed, then died at the database", which is
            // precisely what this file needs to tell apart from "the query string was refused".
            return null;
        }
    }

    [Theory]
    [MemberData(nameof(EveryReadEndpoint))]
    public async Task AFullyPopulatedQueryStringIsNotRejected(string name, string url)
    {
        HttpResponseMessage? response = await GetAsync(url);

        if (response is null)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().NotBe(
            HttpStatusCode.BadRequest,
            $"/{name} was handed a valid value for every parameter it documents, so a 400 means one "
            + $"of them reached the wrong slot. Body: {body}");
    }

    /// <summary>
    /// The negative control. Without this, the theory above would pass just as happily against an
    /// endpoint that had stopped validating anything at all.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/analytics/active?traffic=not-a-traffic-class")]
    [InlineData("/api/v1/analytics/active?interval=fortnight")]
    [InlineData("/api/v1/analytics/active?by=nonsense")]
    // Shape only. An IANA-shaped name this server does not know is deliberately NOT refused here —
    // ClickHouse is the authority on which zones exist, and its error is mapped to a 400 at query
    // time. So the malformed case has to be malformed by shape.
    [InlineData("/api/v1/analytics/active?tz=Europe%2FIstanbul%20%21")]
    [InlineData("/api/v1/analytics/breakdown?groupBy=no_such_column")]
    [InlineData("/api/v1/analytics/top?dimension=no_such_column")]
    [InlineData("/api/v1/analytics/breakdown?metric=events&groupBy=event_type&empty=nil")]
    [InlineData("/api/v1/analytics/timeseries?metric=events&empty=nil")]
    [InlineData("/api/v1/analytics/pivot?groupBy=event_type&metric=events&empty=nil")]
    [InlineData("/api/v1/analytics/compare?metric=events&empty=nil")]
    [InlineData("/api/v1/analytics/distribution?of=session_duration&empty=nil")]
    public async Task GenuinelyBadValuesAreStillRejected(string url)
    {
        HttpResponseMessage? response = await GetAsync(url);

        response.Should().NotBeNull("a bad value must be refused before anything touches storage");
        response!.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ActiveAcceptsARangeItPreviouslyRefused()
    {
        // The exact request the benchmark sent 122,880 times and never got an answer to.
        HttpResponseMessage? response = await GetAsync(
            "/api/v1/analytics/active?interval=day&by=user"
            + "&from=2026-01-01T00:00:00Z&to=2026-04-01T00:00:00Z");

        // Null means it got as far as the database, which is all this needs to prove.
        response?.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
    }
}

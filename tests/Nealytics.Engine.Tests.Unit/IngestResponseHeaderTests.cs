using System.Net;
using System.Text;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// What a server-side caller is told about an event the engine changed or refused.
///
/// The per-field drop rule is right — one misspelled key must not cost the whole event — but on its
/// own it means a caller can integrate against this engine, get a 202 every time, and never learn
/// that half of what they send is being discarded. That is the same shape as an edge worker that
/// returned 204 for three months while dropping every beacon.
///
/// Headers rather than a body, because the status code is load-bearing for anything in front of
/// this and a 202 that sometimes carries a JSON explanation is a worse contract than a 202 that
/// always means the same thing.
///
/// The beacon has no equivalent: sendBeacon discards the response, so nothing there could read a
/// header. Its counterpart is the counter and the log line, which is why those exist.
/// </summary>
public class IngestResponseHeaderTests : IClassFixture<NoDatabaseWebFactory>
{
    private readonly HttpClient _client;

    public IngestResponseHeaderTests(NoDatabaseWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<HttpResponseMessage> TrackAsync(string json)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/telemetry/track")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Project-Key", "unit-key-1");
        return await _client.SendAsync(request);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? string.Join(",", values)
            : string.Empty;

    [Fact]
    public async Task ACleanEventCarriesNoDroppedHeader()
    {
        HttpResponseMessage response = await TrackAsync(
            """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e"}""");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        Header(response, "X-Nealytics-Dropped").Should().BeEmpty(
            "a header on every response would be noise, and noise is not read");
    }

    [Fact]
    public async Task AnUndeclaredDimensionIsNamedInTheHeader_AndTheEventIsStillAccepted()
    {
        HttpResponseMessage response = await TrackAsync(
            """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e","dimensions":{"typo_id":"x"}}""");

        // Accepted, deliberately. Refusing would turn one misspelled key into total loss for that
        // event type, which is exactly what the per-field rule exists to prevent.
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        Header(response, "X-Nealytics-Dropped").Should().Contain("typo_id");
    }

    [Fact]
    public async Task AnUndeclaredMeasureIsNamedToo()
    {
        HttpResponseMessage response = await TrackAsync(
            """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e","measures":{"nope":"1"}}""");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        Header(response, "X-Nealytics-Dropped").Should().Contain("nope");
    }

    [Fact]
    public async Task DimensionsAndMeasuresAppearInOneHeader()
    {
        HttpResponseMessage response = await TrackAsync(
            """
            {"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e",
             "dimensions":{"typo_id":"x"},"measures":{"nope":"1"}}
            """);

        string dropped = Header(response, "X-Nealytics-Dropped");
        dropped.Should().Contain("typo_id");
        dropped.Should().Contain("nope");
    }

    [Fact]
    public async Task AFarFutureTimestampIsRefusedWithItsReason()
    {
        HttpResponseMessage response = await TrackAsync(
            """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e","timestamp":"2099-01-01T00:00:00Z"}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Without the reason, this 400 is indistinguishable from malformed JSON, a missing field
        // or a body over the limit — four different fixes behind one status code.
        Header(response, "X-Nealytics-Rejected").Should().Be("timestamp_too_far_ahead");
    }

    [Fact]
    public async Task AnOverlongSessionIdIsRefusedWithItsReason()
    {
        string tooLong = new('x', 300);

        HttpResponseMessage response = await TrackAsync(
            $$"""{"projectId":"p","tenantId":"t","sessionId":"{{tooLong}}","eventType":"e"}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Header(response, "X-Nealytics-Rejected").Should().Be("field_too_long");
    }

    [Fact]
    public async Task AMissingFieldIsRefusedWithItsReason()
    {
        HttpResponseMessage response = await TrackAsync("""{"projectId":"p","tenantId":"t"}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Header(response, "X-Nealytics-Rejected").Should().Be("missing_session_id");
    }

    [Fact]
    public async Task APinnedKeyCannotWriteAnotherProject()
    {
        // The factory pins unit-key-1 to project "p". A valid key writing a neighbour's project is
        // the case this exists for, and it is otherwise entirely silent: the key authenticates, the
        // payload is well formed, and the rows land somewhere they do not belong.
        HttpResponseMessage response = await TrackAsync(
            """{"projectId":"someone-else","tenantId":"t","sessionId":"s","eventType":"e"}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Header(response, "X-Nealytics-Rejected").Should().Be("project_not_permitted_for_key");
    }

    [Fact]
    public async Task AnUnpinnedKeyStillWritesAnyProject()
    {
        // unit-key-2 has no pin. Pinning is per key so a deployment can adopt it one service at a
        // time rather than as a migration.
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/telemetry/track")
        {
            Content = new StringContent(
                """{"projectId":"anything","tenantId":"t","sessionId":"s","eventType":"e"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-Project-Key", "unit-key-2");

        HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task ABeaconStillAcceptsTheBatchWhenOneElementIsBad()
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/telemetry/beacon?k=unit-key-1")
        {
            Content = new StringContent(
                """
                [{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"a"},
                 {"projectId":"p","tenantId":"t","eventType":"b"},
                 {"projectId":"p","tenantId":"t","sessionId":"s","eventType":"c"}]
                """,
                Encoding.UTF8,
                "application/json"),
        };

        HttpResponseMessage response = await _client.SendAsync(request);

        // The middle element is missing sessionId. The other two must survive: sendBeacon cannot
        // retry, so failing the batch would lose events that were never wrong.
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}

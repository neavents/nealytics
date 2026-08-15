using System.Net;
using System.Text;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// What happens when a body is too big, by both routes it can arrive.
///
/// <c>IngestValidation.ExceedsBodyLimit</c> reads <c>Content-Length</c>, which a chunked request
/// does not send — so that check is skipped entirely for those. Measured before the fix: a chunked
/// body well over the limit was answered <b><c>202</c></b>, read in full and stored.
///
/// Kestrel's <c>Limits.MaxRequestBodySize</c> would have caught it in production, but that is
/// server configuration — it belongs to whoever hosts the process, and no test that does not run
/// Kestrel can see it. Which is precisely why this was worth measuring rather than reasoning about:
/// the plan called it defence-in-depth, and under the test host it was no depth at all.
///
/// The bound now lives in the endpoint (<c>LengthLimitedStream</c>), so it holds on every host and
/// these tests actually prove something.
/// </summary>
public class BodyLimitTests : IClassFixture<NoDatabaseWebFactory>
{
    private readonly HttpClient _client;

    public BodyLimitTests(NoDatabaseWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    private const int OverTheDefaultLimit = 1_048_576 + 4096;

    private static string OversizedJson()
    {
        // Valid JSON, so nothing can refuse it for being malformed — the size has to be the reason.
        StringBuilder sb = new(OverTheDefaultLimit + 128);
        sb.Append("""{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e","metadataJson":" """);
        sb.Append('x', OverTheDefaultLimit);
        sb.Append("\"}");
        return sb.ToString();
    }

    [Fact]
    public async Task WithContentLength_TheEndpointRefusesBeforeReadingAByte()
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/telemetry/track")
        {
            Content = new StringContent(OversizedJson(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Project-Key", "unit-key-1");

        HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task WithoutContentLength_TheEndpointStillBoundsIt()
    {
        // Chunked: no Content-Length, so the cheap check cannot fire. This must still not be
        // accepted, and it must not be a 500 — an oversized body is a client error, and a 500 would
        // page someone at 3am for a request the server handled correctly.
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/telemetry/track")
        {
            Content = new StreamContent(
                new MemoryStream(Encoding.UTF8.GetBytes(OversizedJson()))),
        };
        request.Content.Headers.ContentLength = null;
        request.Content.Headers.Add("Content-Type", "application/json");
        request.Headers.Add("X-Project-Key", "unit-key-1");
        request.Headers.TransferEncodingChunked = true;

        HttpResponseMessage response = await _client.SendAsync(request);

        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(400);
        ((int)response.StatusCode).Should().BeLessThan(500,
            "an oversized body is the caller's mistake, and a 5xx here would both mislead the "
            + "caller and wake someone up for a request the server handled correctly");
    }

    [Fact]
    public async Task AnOrdinarySizedBodyIsUnaffected()
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/telemetry/track")
        {
            Content = new StringContent(
                """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-Project-Key", "unit-key-1");

        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}

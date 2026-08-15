using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Nealytics.Engine.Features.ValidateTelemetry;

namespace Nealytics.Engine.Tests.Unit;

public class ValidateTelemetryEndpointTests : IClassFixture<NoDatabaseWebFactory>
{
    private readonly HttpClient _client;

    public ValidateTelemetryEndpointTests(NoDatabaseWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    private async Task<HttpResponseMessage> PostAsync(string json, string? key = "unit-key-1")
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/telemetry/validate")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (key is not null)
        {
            request.Headers.Add("X-Project-Key", key);
        }

        return await _client.SendAsync(request);
    }

    private const string Minimal =
        """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e"}""";

    [Fact]
    public async Task WithoutAKey_IsUnauthorized()
    {
        (await PostAsync(Minimal, key: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WithABadKey_IsUnauthorized()
    {
        (await PostAsync(Minimal, key: "nope")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AValidPayload_IsAccepted()
    {
        HttpResponseMessage response = await PostAsync(Minimal);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        ValidateTelemetryResponse? body =
            await response.Content.ReadFromJsonAsync<ValidateTelemetryResponse>();

        body!.Accepted.Should().BeTrue();
        body.Columns.Should().Contain("event_id").And.Contain("timestamp");
    }

    [Fact]
    public async Task MissingRequiredFields_AreNamedRatherThanGuessedAt()
    {
        HttpResponseMessage response = await PostAsync("""{"projectId":"p"}""");

        ValidateTelemetryResponse? body =
            await response.Content.ReadFromJsonAsync<ValidateTelemetryResponse>();

        body!.Accepted.Should().BeFalse();
        body.Reason.Should().Contain("tenantId").And.Contain("sessionId").And.Contain("eventType");
    }

    [Fact]
    public async Task AnUndeclaredDimension_IsReportedRatherThanSilentlyDropped()
    {
        HttpResponseMessage response = await PostAsync(
            """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e","dimensions":{"typo_id":"x"}}""");

        ValidateTelemetryResponse? body =
            await response.Content.ReadFromJsonAsync<ValidateTelemetryResponse>();

        body!.Accepted.Should().BeFalse();
        body.DroppedDimensions.Should().Contain("typo_id",
            "the whole point of this endpoint is that one curl tells you the key was wrong, "
            + "instead of a 202 and an empty column three months later");
        body.StoredDimensions.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task AnUndeclaredMeasure_IsReported()
    {
        HttpResponseMessage response = await PostAsync(
            """{"projectId":"p","tenantId":"t","sessionId":"s","eventType":"e","measures":{"dwell_ms":"1200"}}""");

        ValidateTelemetryResponse? body =
            await response.Content.ReadFromJsonAsync<ValidateTelemetryResponse>();

        body!.Accepted.Should().BeFalse();
        body.DroppedMeasures.Should().Contain("dwell_ms");
    }

    [Fact]
    public async Task MalformedJson_Is400WithTheParserReason()
    {
        HttpResponseMessage response = await PostAsync("{not json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ValidateNeverWrites()
    {
        HttpResponseMessage response = await PostAsync(Minimal);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "ClickHouse is unreachable in this fixture, so a 200 here proves the dry run never "
            + "touched storage");
    }
}

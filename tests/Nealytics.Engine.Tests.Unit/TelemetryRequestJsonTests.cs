using System.Text.Json;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed class TelemetryRequestJsonTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"not-a-guid\"")]
    [InlineData("[1,2]")]
    [InlineData("{\"x\":1}")]
    [InlineData("\"11111111-1111-4111-8111-111111111111\"")]
    public void CallerFieldsCannotControlInternalStorageIdentity(string ignored)
    {
        string json = "{\"eventId\":\"00000000-0000-0000-0000-000000000000\",\"projectId\":\"p\","
            + "\"tenantId\":\"t\",\"eventType\":\"e\",\"_walRecordId\":" + ignored + "}";
        var payload = JsonSerializer.Deserialize(json, TelemetryRequestJson.Payload)!;
        Assert.Equal(Guid.Empty, payload.EventId);
        Assert.Equal(Guid.Empty, payload.WalRecordId);
        Assert.Equal("p", payload.ProjectId);
        Assert.Equal("t", payload.TenantId);
        Assert.Equal("e", payload.EventType);
        Assert.DoesNotContain(TelemetryRequestJson.Payload.Properties, property => property.Name == "_walRecordId");
        Assert.Equal(typeof(string), TelemetryRequestJson.Payload.Options.GetTypeInfo(typeof(string)).Type);
    }

    [Fact]
    public void RequestMetadataDoesNotAlterThePersistedWalContract()
    {
        Guid identity = Guid.NewGuid();
        var payload = new GlobalTelemetryPayload { WalRecordId = identity, ProjectId = "p" };
        string encoded = JsonSerializer.Serialize(payload, TelemetryAotContext.Default.GlobalTelemetryPayload);
        Assert.Contains("\"_walRecordId\":\"" + identity + "\"", encoded);
        var recovered = JsonSerializer.Deserialize(encoded, TelemetryAotContext.Default.GlobalTelemetryPayload)!;
        Assert.Equal(identity, recovered.WalRecordId);
        Assert.Same(TelemetryRequestJson.Payload, TelemetryRequestJson.Payload);
        Assert.True(TelemetryRequestJson.Payload.IsReadOnly);
    }
}

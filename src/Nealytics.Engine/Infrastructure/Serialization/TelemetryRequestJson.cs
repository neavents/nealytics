namespace Nealytics.Engine.Infrastructure.Serialization;

using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

internal static class TelemetryRequestJson
{
    public static JsonTypeInfo<GlobalTelemetryPayload> Payload { get; } = Create();

    private static JsonTypeInfo<GlobalTelemetryPayload> Create()
    {
        JsonSerializerOptions options = new JsonSerializerOptions(TelemetryAotContext.Default.Options)
        {
            TypeInfoResolver = TelemetryAotContext.Default.WithAddedModifier(RemoveStorageIdentity)
        };
        options.MakeReadOnly();
        return (JsonTypeInfo<GlobalTelemetryPayload>)options.GetTypeInfo(typeof(GlobalTelemetryPayload));
    }

    private static void RemoveStorageIdentity(JsonTypeInfo information)
    {
        if (information.Type != typeof(GlobalTelemetryPayload))
        {
            return;
        }
        JsonPropertyInfo identity = information.Properties.Single(property => property.Name == "_walRecordId");
        information.Properties.Remove(identity);
    }
}

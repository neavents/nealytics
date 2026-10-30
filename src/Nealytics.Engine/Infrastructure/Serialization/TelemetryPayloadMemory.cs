namespace Nealytics.Engine.Infrastructure.Serialization;

using System;
using System.Collections.Generic;

internal static class TelemetryPayloadMemory
{
    public static int Estimate(GlobalTelemetryPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        long bytes = 512;
        bytes += Text(payload.ProjectId) + Text(payload.TenantId) + Text(payload.SessionId) + Text(payload.UserId);
        bytes += Text(payload.EventType) + Text(payload.ObjectId) + Text(payload.TrafficClass);
        bytes += Text(payload.PagePath) + Text(payload.Referrer) + Text(payload.DeviceClass);
        bytes += Text(payload.Os) + Text(payload.Browser) + Text(payload.Country) + Text(payload.MetadataJson);
        bytes += Dictionary(payload.Dimensions) + Dictionary(payload.Measures);
        return (int)Math.Min(int.MaxValue, bytes);
    }

    private static long Text(string? value) => value is null ? 0 : 32L + value.Length * 2L;

    private static long Dictionary(Dictionary<string, string>? values)
    {
        if (values is null)
        {
            return 0;
        }
        long bytes = 128;
        foreach (KeyValuePair<string, string> value in values)
        {
            bytes += 96 + Text(value.Key) + Text(value.Value);
        }
        return bytes;
    }
}

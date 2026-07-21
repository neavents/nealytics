namespace Nealytics.Engine.Features.BatchProcessor;

using System;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Serialization;

public static class TelemetryColumnMapper
{
    public static void Fill(
        IReadOnlyList<GlobalTelemetryPayload> batch,
        int count,
        Guid[] eventIds,
        string[] projectIds,
        string[] tenantIds,
        string[] sessionIds,
        string?[] userIds,
        string[] eventTypes,
        string?[] itemIds,
        string[] metadataJsons,
        DateTimeOffset[] timestamps)
    {
        for (int i = 0; i < count; i++)
        {
            GlobalTelemetryPayload payload = batch[i];
            eventIds[i] = payload.EventId;
            projectIds[i] = payload.ProjectId;
            tenantIds[i] = payload.TenantId;
            sessionIds[i] = payload.SessionId;
            userIds[i] = payload.UserId;
            eventTypes[i] = payload.EventType;
            itemIds[i] = payload.ItemId;
            metadataJsons[i] = payload.MetadataJson;
            timestamps[i] = TelemetryInsertMath.ToClickHouseTimestamp(payload.Timestamp);
        }
    }

    public static Dictionary<string, object?> BuildColumns(
        int count,
        Guid[] eventIds,
        string[] projectIds,
        string[] tenantIds,
        string[] sessionIds,
        string?[] userIds,
        string[] eventTypes,
        string?[] itemIds,
        string[] metadataJsons,
        DateTimeOffset[] timestamps)
    {
        return new Dictionary<string, object?>(9)
        {
            ["event_id"] = new ArraySegment<Guid>(eventIds, 0, count),
            ["project_id"] = new ArraySegment<string>(projectIds, 0, count),
            ["tenant_id"] = new ArraySegment<string>(tenantIds, 0, count),
            ["session_id"] = new ArraySegment<string>(sessionIds, 0, count),
            ["user_id"] = new ArraySegment<string?>(userIds, 0, count),
            ["event_type"] = new ArraySegment<string>(eventTypes, 0, count),
            ["item_id"] = new ArraySegment<string?>(itemIds, 0, count),
            ["metadata_json"] = new ArraySegment<string>(metadataJsons, 0, count),
            ["timestamp"] = new ArraySegment<DateTimeOffset>(timestamps, 0, count)
        };
    }
}

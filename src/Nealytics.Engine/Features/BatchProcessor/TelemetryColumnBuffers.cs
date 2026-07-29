namespace Nealytics.Engine.Features.BatchProcessor;

using System;
using System.Buffers;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Serialization;

/// <summary>
/// Owns the pooled column arrays for one insert batch.
///
/// These were nine locals in <see cref="ClickHouseBatchWriter"/>, rented at the top and returned
/// in a <c>finally</c>, with the array list repeated three times over — rent, fill, build, return.
/// Promoting menu / section / table / device / os / browser / country out of <c>metadata_json</c>
/// takes that to sixteen, and four hand-maintained copies of a sixteen-item list is a rent that
/// eventually goes unreturned or a column that ends up misaligned by one.
///
/// The array list now appears once. Disposal returns every buffer, and reference-typed arrays are
/// cleared on return so a pooled array cannot hand a later batch someone else's tenant id.
/// </summary>
internal sealed class TelemetryColumnBuffers : IDisposable
{
    private readonly int _count;

    internal readonly Guid[] EventIds;
    internal readonly string[] ProjectIds;
    internal readonly string[] TenantIds;
    internal readonly string[] SessionIds;
    internal readonly string?[] UserIds;
    internal readonly string[] EventTypes;
    internal readonly string?[] ItemIds;
    internal readonly string?[] MenuIds;
    internal readonly string?[] SectionIds;
    internal readonly string?[] TableIds;
    internal readonly string[] DeviceClasses;
    internal readonly string[] OperatingSystems;
    internal readonly string[] Browsers;
    internal readonly string[] Countries;
    internal readonly string[] MetadataJsons;
    internal readonly DateTimeOffset[] Timestamps;

    internal TelemetryColumnBuffers(int count)
    {
        _count = count;

        EventIds = ArrayPool<Guid>.Shared.Rent(count);
        ProjectIds = ArrayPool<string>.Shared.Rent(count);
        TenantIds = ArrayPool<string>.Shared.Rent(count);
        SessionIds = ArrayPool<string>.Shared.Rent(count);
        UserIds = ArrayPool<string?>.Shared.Rent(count);
        EventTypes = ArrayPool<string>.Shared.Rent(count);
        ItemIds = ArrayPool<string?>.Shared.Rent(count);
        MenuIds = ArrayPool<string?>.Shared.Rent(count);
        SectionIds = ArrayPool<string?>.Shared.Rent(count);
        TableIds = ArrayPool<string?>.Shared.Rent(count);
        DeviceClasses = ArrayPool<string>.Shared.Rent(count);
        OperatingSystems = ArrayPool<string>.Shared.Rent(count);
        Browsers = ArrayPool<string>.Shared.Rent(count);
        Countries = ArrayPool<string>.Shared.Rent(count);
        MetadataJsons = ArrayPool<string>.Shared.Rent(count);
        Timestamps = ArrayPool<DateTimeOffset>.Shared.Rent(count);
    }

    /// <summary>Copies one batch into the column arrays, in insert order.</summary>
    internal void Fill(IReadOnlyList<GlobalTelemetryPayload> batch)
    {
        for (int i = 0; i < _count; i++)
        {
            GlobalTelemetryPayload payload = batch[i];

            EventIds[i] = payload.EventId;
            ProjectIds[i] = payload.ProjectId;
            TenantIds[i] = payload.TenantId;
            SessionIds[i] = payload.SessionId;
            UserIds[i] = payload.UserId;
            EventTypes[i] = payload.EventType;
            ItemIds[i] = payload.ItemId;
            MenuIds[i] = payload.MenuId;
            SectionIds[i] = payload.SectionId;
            TableIds[i] = payload.TableId;

            // LowCardinality columns: empty string rather than null keeps GROUP BY total.
            DeviceClasses[i] = payload.DeviceClass ?? string.Empty;
            OperatingSystems[i] = payload.Os ?? string.Empty;
            Browsers[i] = payload.Browser ?? string.Empty;
            Countries[i] = payload.Country ?? string.Empty;

            MetadataJsons[i] = payload.MetadataJson;
            Timestamps[i] = TelemetryInsertMath.ToClickHouseTimestamp(payload.Timestamp);
        }
    }

    /// <summary>
    /// Column name to buffer. Keys must match <see cref="ClickHouseBatchWriter.InsertColumns"/>
    /// exactly — the driver binds by name, and a typo surfaces as a rejected batch.
    /// </summary>
    internal Dictionary<string, object?> BuildColumns() => new(16)
    {
        ["event_id"] = new ArraySegment<Guid>(EventIds, 0, _count),
        ["project_id"] = new ArraySegment<string>(ProjectIds, 0, _count),
        ["tenant_id"] = new ArraySegment<string>(TenantIds, 0, _count),
        ["session_id"] = new ArraySegment<string>(SessionIds, 0, _count),
        ["user_id"] = new ArraySegment<string?>(UserIds, 0, _count),
        ["event_type"] = new ArraySegment<string>(EventTypes, 0, _count),
        ["item_id"] = new ArraySegment<string?>(ItemIds, 0, _count),
        ["menu_id"] = new ArraySegment<string?>(MenuIds, 0, _count),
        ["section_id"] = new ArraySegment<string?>(SectionIds, 0, _count),
        ["table_id"] = new ArraySegment<string?>(TableIds, 0, _count),
        ["device_class"] = new ArraySegment<string>(DeviceClasses, 0, _count),
        ["os"] = new ArraySegment<string>(OperatingSystems, 0, _count),
        ["browser"] = new ArraySegment<string>(Browsers, 0, _count),
        ["country"] = new ArraySegment<string>(Countries, 0, _count),
        ["metadata_json"] = new ArraySegment<string>(MetadataJsons, 0, _count),
        ["timestamp"] = new ArraySegment<DateTimeOffset>(Timestamps, 0, _count),
    };

    public void Dispose()
    {
        ArrayPool<Guid>.Shared.Return(EventIds);
        ArrayPool<string>.Shared.Return(ProjectIds, true);
        ArrayPool<string>.Shared.Return(TenantIds, true);
        ArrayPool<string>.Shared.Return(SessionIds, true);
        ArrayPool<string?>.Shared.Return(UserIds, true);
        ArrayPool<string>.Shared.Return(EventTypes, true);
        ArrayPool<string?>.Shared.Return(ItemIds, true);
        ArrayPool<string?>.Shared.Return(MenuIds, true);
        ArrayPool<string?>.Shared.Return(SectionIds, true);
        ArrayPool<string?>.Shared.Return(TableIds, true);
        ArrayPool<string>.Shared.Return(DeviceClasses, true);
        ArrayPool<string>.Shared.Return(OperatingSystems, true);
        ArrayPool<string>.Shared.Return(Browsers, true);
        ArrayPool<string>.Shared.Return(Countries, true);
        ArrayPool<string>.Shared.Return(MetadataJsons, true);
        ArrayPool<DateTimeOffset>.Shared.Return(Timestamps);
    }
}

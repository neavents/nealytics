namespace Nealytics.Engine.Features.BatchProcessor;

using System;
using System.Buffers;
using System.Collections.Generic;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;

/// <summary>
/// Owns the pooled column arrays for one insert batch.
///
/// These were nine locals in <see cref="ClickHouseBatchWriter"/>, rented at the top and returned
/// in a <c>finally</c>, with the array list repeated three times over — rent, fill, build, return.
/// Four hand-maintained copies of one list is a rent that eventually goes unreturned or a column
/// that ends up misaligned by one.
///
/// The core array list now appears once, and the deployment's dimensions are not a list in this
/// file at all: they come from <see cref="DimensionRegistry"/>, one
/// <see cref="DimensionColumnBuffer"/> each, built in registry order from the same
/// <see cref="TelemetryColumnLayout"/> the INSERT statement is built from.
///
/// Disposal returns every buffer, and reference-typed arrays are cleared on return so a pooled
/// array cannot hand a later batch someone else's tenant id.
/// </summary>
internal sealed class TelemetryColumnBuffers : IDisposable
{
    private readonly int _count;
    private readonly TelemetryColumnLayout _layout;
    private readonly DimensionColumnBuffer[] _dimensionBuffers;

    internal readonly Guid[] EventIds;
    internal readonly string[] ProjectIds;
    internal readonly string[] TenantIds;
    internal readonly string[] SessionIds;
    internal readonly string?[] UserIds;
    internal readonly string[] EventTypes;
    internal readonly string?[] ObjectIds;
    internal readonly string[] DeviceClasses;
    internal readonly string[] OperatingSystems;
    internal readonly string[] Browsers;
    internal readonly string[] Countries;
    internal readonly string[] MetadataJsons;
    internal readonly DateTimeOffset[] Timestamps;

    internal TelemetryColumnBuffers(int count, TelemetryColumnLayout layout)
    {
        _count = count;
        _layout = layout;

        EventIds = ArrayPool<Guid>.Shared.Rent(count);
        ProjectIds = ArrayPool<string>.Shared.Rent(count);
        TenantIds = ArrayPool<string>.Shared.Rent(count);
        SessionIds = ArrayPool<string>.Shared.Rent(count);
        UserIds = ArrayPool<string?>.Shared.Rent(count);
        EventTypes = ArrayPool<string>.Shared.Rent(count);
        ObjectIds = ArrayPool<string?>.Shared.Rent(count);
        DeviceClasses = ArrayPool<string>.Shared.Rent(count);
        OperatingSystems = ArrayPool<string>.Shared.Rent(count);
        Browsers = ArrayPool<string>.Shared.Rent(count);
        Countries = ArrayPool<string>.Shared.Rent(count);
        MetadataJsons = ArrayPool<string>.Shared.Rent(count);
        Timestamps = ArrayPool<DateTimeOffset>.Shared.Rent(count);

        _dimensionBuffers = new DimensionColumnBuffer[layout.Dimensions.Count];
        for (int i = 0; i < layout.Dimensions.Count; i++)
        {
            _dimensionBuffers[i] = DimensionColumnBuffer.Create(layout.Dimensions[i], count);
        }
    }

    internal IReadOnlyList<DimensionColumnBuffer> DimensionBuffers => _dimensionBuffers;

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
            ObjectIds[i] = payload.ObjectId;

            // LowCardinality columns: empty string rather than null keeps GROUP BY total.
            DeviceClasses[i] = payload.DeviceClass ?? string.Empty;
            OperatingSystems[i] = payload.Os ?? string.Empty;
            Browsers[i] = payload.Browser ?? string.Empty;
            Countries[i] = payload.Country ?? string.Empty;

            MetadataJsons[i] = payload.MetadataJson;
            Timestamps[i] = TelemetryInsertMath.ToClickHouseTimestamp(payload.Timestamp);

            // Driven by the registry, never by the payload's keys. A payload carrying a key that
            // is no longer declared — a WAL record written before a dimension was retired, say —
            // simply is not read, rather than widening the insert to a column that may not exist.
            Dictionary<string, string>? dimensions = payload.Dimensions;
            for (int d = 0; d < _dimensionBuffers.Length; d++)
            {
                DimensionColumnBuffer buffer = _dimensionBuffers[d];
                string? value = null;
                dimensions?.TryGetValue(buffer.Dimension.Name, out value);
                buffer.Set(i, value);
            }
        }
    }

    /// <summary>
    /// Column name to buffer, built by walking <see cref="TelemetryColumnLayout.ColumnNames"/> —
    /// the same list <see cref="ClickHouseBatchWriter"/> builds its INSERT from. The driver binds
    /// by name, so a key that is not in that list is a rejected batch, and a name in the list with
    /// no key here is the same.
    /// </summary>
    internal Dictionary<string, object?> BuildColumns()
    {
        Dictionary<string, object?> columns = new(_layout.ColumnNames.Count, StringComparer.Ordinal)
        {
            ["event_id"] = new ArraySegment<Guid>(EventIds, 0, _count),
            ["project_id"] = new ArraySegment<string>(ProjectIds, 0, _count),
            ["tenant_id"] = new ArraySegment<string>(TenantIds, 0, _count),
            ["session_id"] = new ArraySegment<string>(SessionIds, 0, _count),
            ["user_id"] = new ArraySegment<string?>(UserIds, 0, _count),
            ["event_type"] = new ArraySegment<string>(EventTypes, 0, _count),
            ["object_id"] = new ArraySegment<string?>(ObjectIds, 0, _count),
            ["device_class"] = new ArraySegment<string>(DeviceClasses, 0, _count),
            ["os"] = new ArraySegment<string>(OperatingSystems, 0, _count),
            ["browser"] = new ArraySegment<string>(Browsers, 0, _count),
            ["country"] = new ArraySegment<string>(Countries, 0, _count),
            ["metadata_json"] = new ArraySegment<string>(MetadataJsons, 0, _count),
            ["timestamp"] = new ArraySegment<DateTimeOffset>(Timestamps, 0, _count),
        };

        for (int i = 0; i < _dimensionBuffers.Length; i++)
        {
            DimensionColumnBuffer buffer = _dimensionBuffers[i];
            columns[buffer.Dimension.Name] = buffer.BuildSegment(_count);
        }

        return columns;
    }

    public void Dispose()
    {
        ArrayPool<Guid>.Shared.Return(EventIds);
        ArrayPool<string>.Shared.Return(ProjectIds, true);
        ArrayPool<string>.Shared.Return(TenantIds, true);
        ArrayPool<string>.Shared.Return(SessionIds, true);
        ArrayPool<string?>.Shared.Return(UserIds, true);
        ArrayPool<string>.Shared.Return(EventTypes, true);
        ArrayPool<string?>.Shared.Return(ObjectIds, true);
        ArrayPool<string>.Shared.Return(DeviceClasses, true);
        ArrayPool<string>.Shared.Return(OperatingSystems, true);
        ArrayPool<string>.Shared.Return(Browsers, true);
        ArrayPool<string>.Shared.Return(Countries, true);
        ArrayPool<string>.Shared.Return(MetadataJsons, true);
        ArrayPool<DateTimeOffset>.Shared.Return(Timestamps);

        for (int i = 0; i < _dimensionBuffers.Length; i++)
        {
            _dimensionBuffers[i].Dispose();
        }
    }
}

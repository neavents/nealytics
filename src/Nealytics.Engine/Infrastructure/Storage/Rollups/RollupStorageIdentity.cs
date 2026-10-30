namespace Nealytics.Engine.Infrastructure.Storage.Rollups;

using System;

internal sealed record RollupStorageIdentity
{
    public RollupStorageIdentity(Guid sourceId, Guid tableId, Guid viewId, string declaredDefinition, string storedDefinition)
    {
        if (sourceId == Guid.Empty || tableId == Guid.Empty || viewId == Guid.Empty)
        {
            throw new InvalidOperationException("Rollup readiness requires existing source, table and view with persistent UUIDs.");
        }
        SourceId = sourceId;
        TableId = tableId;
        ViewId = viewId;
        DeclaredDefinition = declaredDefinition;
        StoredDefinition = storedDefinition;
    }

    public Guid SourceId { get; }
    public Guid TableId { get; }
    public Guid ViewId { get; }
    public string DeclaredDefinition { get; }
    public string StoredDefinition { get; }
}

namespace Nealytics.Engine.Infrastructure.Security;

public enum IngestionKeyScope
{
    Unscoped,
    Public,
    Server,
}

public sealed class IngestionKeyPolicy
{
    public IngestionKeyPolicy(IngestionKeyScope scope, string? pinnedProject, EventTypePattern allowedEventTypes)
    {
        Scope = scope;
        PinnedProject = pinnedProject;
        AllowedEventTypes = allowedEventTypes;
    }

    public static IngestionKeyPolicy Unpinned { get; } = new(IngestionKeyScope.Unscoped, null, EventTypePattern.None);

    public IngestionKeyScope Scope { get; }

    public string? PinnedProject { get; }

    public EventTypePattern AllowedEventTypes { get; }

    public bool IsServer => Scope == IngestionKeyScope.Server;

    public bool PermitsEventType(string eventType) =>
        AllowedEventTypes.IsEmpty || AllowedEventTypes.Matches(eventType);
}

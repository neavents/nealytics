namespace Nealytics.Engine.Infrastructure.Security;

using System;
using System.Collections.Frozen;

internal sealed record ProjectKeyBoundary(FrozenSet<string> Projects, bool AllowLegacy);

public enum IngestionKeyScope
{
    Unscoped,
    Public,
    Server,
}

public sealed class IngestionKeyPolicy
{
    private readonly ProjectKeyBoundary? _projects;

    public IngestionKeyPolicy(IngestionKeyScope scope, string? pinnedProject, EventTypePattern allowedEventTypes)
    {
        Scope = scope;
        PinnedProject = pinnedProject;
        AllowedEventTypes = allowedEventTypes;
    }

    internal IngestionKeyPolicy(IngestionKeyScope scope, string? pinnedProject, EventTypePattern allowedEventTypes, ProjectKeyBoundary? projects)
        : this(scope, pinnedProject, allowedEventTypes)
    {
        _projects = projects;
    }

    public static IngestionKeyPolicy Unpinned { get; } = new(IngestionKeyScope.Unscoped, null, EventTypePattern.None);

    public IngestionKeyScope Scope { get; }

    public string? PinnedProject { get; }

    public EventTypePattern AllowedEventTypes { get; }

    public bool IsServer => Scope == IngestionKeyScope.Server;

    internal bool PermitsProject(string projectId)
    {
        if (PinnedProject is not null && !string.Equals(PinnedProject, projectId, StringComparison.Ordinal))
        {
            return false;
        }
        return _projects is null || (_projects.Projects.Contains(projectId) ? PinnedProject is not null : _projects.AllowLegacy);
    }

    public bool PermitsEventType(string eventType) =>
        AllowedEventTypes.IsEmpty || AllowedEventTypes.Matches(eventType);
}

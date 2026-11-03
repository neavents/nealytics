namespace Nealytics.Engine.Infrastructure.Security;

using Microsoft.AspNetCore.Http;
using System;
using Nealytics.Engine.Infrastructure.Configuration;

public sealed class ProjectRequestContext
{
    internal static readonly object ItemKey = new();

    public ProjectRequestContext(IHttpContextAccessor accessor, ProjectCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(catalog);
        object? selected = null;
        accessor.HttpContext?.Items.TryGetValue(ItemKey, out selected);
        Current = selected as ProjectContext ?? catalog.Default;
    }

    public ProjectContext Current { get; }
}

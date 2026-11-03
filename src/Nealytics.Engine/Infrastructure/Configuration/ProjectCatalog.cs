namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

public sealed class ProjectCatalog
{
    private readonly FrozenDictionary<string, ProjectContext> _contexts;
    private readonly bool _allowLegacy;

    public ProjectCatalog(TelemetryEngineOptions options, ProjectContext baseline)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(baseline);
        if (options.MaxProjectSchemas is < 1 or > 1024)
        {
            throw new InvalidOperationException("MaxProjectSchemas must be between 1 and 1024.");
        }
        IReadOnlyList<ProjectSchemaOptions> declarations = options.ProjectSchemas ?? [];
        if (declarations.Count > options.MaxProjectSchemas)
        {
            throw new InvalidOperationException("ProjectSchemas exceeds MaxProjectSchemas.");
        }
        Default = baseline;
        _allowLegacy = declarations.Count == 0 || options.AllowLegacyProjectSchema;
        List<ProjectContext> contexts = new(declarations.Count);
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (ProjectSchemaOptions declaration in declarations)
        {
            string? id = declaration?.ProjectId?.Trim();
            if (string.IsNullOrEmpty(id) || id.Length > 256 || declaration!.Version != 1 || !names.Add(id))
            {
                throw new InvalidOperationException("Project schemas require unique nonblank identifiers up to 256 characters and version 1.");
            }
            contexts.Add(ProjectContext.Compile(id, declaration.Apply(options)));
        }
        Storage = contexts.Count == 0
            ? new StorageSchema(baseline.Dimensions, baseline.Measures, baseline.TenantAttributes, baseline.Rollups)
            : ProjectSchemaUnion.Compile(options, baseline, contexts);
        if (contexts.Count != 0)
        {
            Default = baseline with
            {
                Rollups = Storage.Rollups.ShareReadiness(baseline.Rollups)
            };
        }
        List<ProjectContext> shared = new(contexts.Count);
        foreach (ProjectContext context in contexts)
        {
            shared.Add(context with { Rollups = Storage.Rollups.ShareReadiness(context.Rollups) });
        }
        Declared = Array.AsReadOnly(shared.ToArray());
        _contexts = shared.ToFrozenDictionary(context => context.ProjectId!, StringComparer.Ordinal);
    }

    public ProjectContext Default { get; }
    public StorageSchema Storage { get; }
    public IReadOnlyList<ProjectContext> Declared { get; }
    public bool Enabled => Declared.Count != 0;

    public ProjectContext? Resolve(string? projectId)
    {
        ProjectContext? context = null;
        return projectId is not null && _contexts.TryGetValue(projectId, out context) ? context
            : _allowLegacy ? Default : null;
    }
}

namespace Nealytics.Engine.Infrastructure.Security;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;

public sealed partial class ApiKeyValidator
{
    private readonly FrozenSet<string> _validKeys;
    private readonly FrozenDictionary<string, string> _pinnedProjects;

    public ApiKeyValidator(IOptions<TelemetryEngineOptions> options, ILogger<ApiKeyValidator> logger)
    {
        string rawKeys = options.Value.AllowedProjectKeys;
        _validKeys = string.IsNullOrWhiteSpace(rawKeys)
            ? FrozenSet<string>.Empty
            : rawKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToFrozenSet(StringComparer.Ordinal);

        if (_validKeys.Count == 0)
        {
            LogNoKeysConfigured(logger);
        }

        Dictionary<string, string> pins = new(StringComparer.Ordinal);

        foreach (ProjectKeyOptions declared in options.Value.Projects ?? [])
        {
            if (string.IsNullOrWhiteSpace(declared.Key) || string.IsNullOrWhiteSpace(declared.ProjectId))
            {
                throw new InvalidOperationException(
                    "TelemetryEngine:Projects has an entry missing Key or ProjectId. A half-written "
                    + "pin would silently protect nothing, so it is refused rather than ignored.");
            }

            if (!_validKeys.Contains(declared.Key))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Projects pins a key to project '{declared.ProjectId}', but that "
                    + "key is not in TelemetryEngine:AllowedProjectKeys, so it can never be used. "
                    + "Add it there or remove the pin.");
            }

            if (!pins.TryAdd(declared.Key, declared.ProjectId))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Projects pins the same key to more than one project "
                    + $"('{pins[declared.Key]}' and '{declared.ProjectId}'). One of them would be "
                    + "silently ignored.");
            }
        }

        _pinnedProjects = pins.ToFrozenDictionary(StringComparer.Ordinal);

        if (_pinnedProjects.Count > 0)
        {
            LogProjectPinning(logger, _pinnedProjects.Count, _validKeys.Count);
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "No project keys configured. All ingestion requests will be rejected.")]
    private static partial void LogNoKeysConfigured(ILogger logger);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
        Message = "{PinnedCount} of {KeyCount} project key(s) are pinned to a project. The rest may "
            + "write any projectId, which is the default.")]
    private static partial void LogProjectPinning(ILogger logger, int pinnedCount, int keyCount);

    public bool IsValid(string key) => _validKeys.Contains(key);

    public bool MayWriteProject(string key, string projectId) =>
        !_pinnedProjects.TryGetValue(key, out string? pinned)
        || string.Equals(pinned, projectId, StringComparison.Ordinal);
}

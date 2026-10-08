namespace Nealytics.Engine.Infrastructure.Security;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;

public sealed partial class ApiKeyValidator
{
    private readonly FrozenDictionary<string, IngestionKeyPolicy> _policies;

    public ApiKeyValidator(IOptions<TelemetryEngineOptions> options, ILogger<ApiKeyValidator> logger)
    {
        TelemetryEngineOptions engine = options.Value;
        string rawKeys = engine.AllowedProjectKeys;

        HashSet<string> unscoped = string.IsNullOrWhiteSpace(rawKeys)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                rawKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);

        Dictionary<string, (IngestionKeyScope Scope, string? Pin, EventTypePattern EventTypes)> declared =
            new(StringComparer.Ordinal);

        foreach (string key in unscoped)
        {
            declared[key] = (IngestionKeyScope.Unscoped, null, EventTypePattern.None);
        }

        List<IngestionKeyOptions> scopedKeys = engine.IngestionKeys ?? [];

        for (int i = 0; i < scopedKeys.Count; i++)
        {
            IngestionKeyOptions scoped = scopedKeys[i];
            string key = scoped.Key?.Trim() ?? string.Empty;

            if (key.Length == 0)
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:IngestionKeys[{i}] has no Key. A scoped key with no value protects nothing.");
            }

            IngestionKeyScope scope = (scoped.Scope?.Trim() ?? string.Empty).ToLowerInvariant() switch
            {
                "public" => IngestionKeyScope.Public,
                "server" => IngestionKeyScope.Server,
                _ => throw new InvalidOperationException(
                    $"TelemetryEngine:IngestionKeys[{i}] declares scope '{scoped.Scope}'. It must be public "
                    + "or server; a key that should behave as before belongs in AllowedProjectKeys."),
            };

            if (declared.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:IngestionKeys[{i}] declares a key that is already declared, either in "
                    + "AllowedProjectKeys or earlier in IngestionKeys. One key has one scope.");
            }

            string? pin = string.IsNullOrWhiteSpace(scoped.ProjectId) ? null : scoped.ProjectId.Trim();
            EventTypePattern eventTypes =
                EventTypePattern.Parse(scoped.EventTypes, $"TelemetryEngine:IngestionKeys[{i}]:EventTypes");

            declared[key] = (scope, pin, eventTypes);
        }

        if (declared.Count == 0)
        {
            LogNoKeysConfigured(logger);
        }

        int pinned = 0;

        foreach (ProjectKeyOptions declaredPin in engine.Projects ?? [])
        {
            if (string.IsNullOrWhiteSpace(declaredPin.Key) || string.IsNullOrWhiteSpace(declaredPin.ProjectId))
            {
                throw new InvalidOperationException(
                    "TelemetryEngine:Projects has an entry missing Key or ProjectId. A half-written "
                    + "pin would silently protect nothing, so it is refused rather than ignored.");
            }

            if (!declared.TryGetValue(declaredPin.Key, out (IngestionKeyScope Scope, string? Pin, EventTypePattern EventTypes) entry))
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Projects pins a key to project '{declaredPin.ProjectId}', but that "
                    + "key is not in TelemetryEngine:AllowedProjectKeys, so it can never be used. "
                    + "Add it there or remove the pin.");
            }

            if (entry.Pin is not null)
            {
                throw new InvalidOperationException(
                    $"TelemetryEngine:Projects pins the same key to more than one project "
                    + $"('{entry.Pin}' and '{declaredPin.ProjectId}'). One of them would be "
                    + "silently ignored.");
            }

            declared[declaredPin.Key] = (entry.Scope, declaredPin.ProjectId, entry.EventTypes);
        }

        Dictionary<string, IngestionKeyPolicy> policies = new(declared.Count, StringComparer.Ordinal);

        foreach (KeyValuePair<string, (IngestionKeyScope Scope, string? Pin, EventTypePattern EventTypes)> entry in declared)
        {
            if (entry.Value.Pin is not null)
            {
                pinned++;
            }

            policies[entry.Key] = entry.Value.Scope == IngestionKeyScope.Unscoped && entry.Value.Pin is null
                ? IngestionKeyPolicy.Unpinned
                : new IngestionKeyPolicy(entry.Value.Scope, entry.Value.Pin, entry.Value.EventTypes);
        }

        _policies = policies.ToFrozenDictionary(StringComparer.Ordinal);

        if (pinned > 0)
        {
            LogProjectPinning(logger, pinned, _policies.Count);
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "No project keys configured. All ingestion requests will be rejected.")]
    private static partial void LogNoKeysConfigured(ILogger logger);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
        Message = "{PinnedCount} of {KeyCount} project key(s) are pinned to a project. The rest may "
            + "write any projectId, which is the default.")]
    private static partial void LogProjectPinning(ILogger logger, int pinnedCount, int keyCount);

    public bool IsValid(string? key) => key is not null && _policies.ContainsKey(key);

    public bool TryResolve(string? key, [NotNullWhen(true)] out IngestionKeyPolicy? policy)
    {
        if (key is null)
        {
            policy = null;
            return false;
        }

        return _policies.TryGetValue(key, out policy);
    }

    public bool IsServerKey(string key) =>
        _policies.TryGetValue(key, out IngestionKeyPolicy? policy) && policy.IsServer;

    public bool MayWriteProject(string key, string projectId) =>
        !_policies.TryGetValue(key, out IngestionKeyPolicy? policy)
        || MayWriteProject(policy, projectId);

    public static bool MayWriteProject(IngestionKeyPolicy policy, string projectId) =>
        policy.PinnedProject is null || string.Equals(policy.PinnedProject, projectId, StringComparison.Ordinal);
}

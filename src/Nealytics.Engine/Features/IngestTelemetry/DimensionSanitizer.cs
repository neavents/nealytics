namespace Nealytics.Engine.Features.IngestTelemetry;

using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Serialization;

/// <summary>
/// Strips dimension keys the deployment has not declared, before the payload reaches the WAL.
///
/// Two rules, both deliberate.
///
/// <b>Per field, never per event or per batch.</b> The client is <c>navigator.sendBeacon</c>: it
/// cannot retry and its caller cannot react to a rejection. Losing a whole session's events
/// because one key was misspelled is far worse than losing one field.
///
/// <b>Counted and logged, never silent.</b> An unregistered dimension disappearing without a
/// trace is the exact failure the declared-dimension design exists to end. A Cloudflare Worker in
/// this estate returned <c>204</c> for three months while discarding every analytics beacon, and
/// the only evidence was a table that stopped growing.
///
/// Sanitizing happens before the WAL append so a replay cannot reintroduce a key the registry
/// rejected — the log on disk and the declared schema stay one story.
/// </summary>
public sealed partial class DimensionSanitizer
{
    /// <summary>Cap on names quoted in a single log line, so a hostile payload cannot flood the log.</summary>
    private const int MaxNamesPerLogLine = 8;

    private readonly DimensionRegistry _registry;
    private readonly ILogger<DimensionSanitizer> _logger;

    public DimensionSanitizer(DimensionRegistry registry, ILogger<DimensionSanitizer> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    [LoggerMessage(EventId = 9201, Level = LogLevel.Warning,
        Message = "Dropped {DroppedCount} undeclared dimension(s) from a {EventType} event on project "
            + "'{ProjectId}': {Names}. Declare them under TelemetryEngine:Dimensions or the values "
            + "will keep being discarded.")]
    private static partial void LogUndeclaredDimensions(
        ILogger logger, int droppedCount, string eventType, string projectId, string names);

    /// <summary>
    /// Removes undeclared keys from <paramref name="payload"/> in place. Returns how many were
    /// dropped.
    /// </summary>
    public int Sanitize(GlobalTelemetryPayload payload) => Sanitize(payload, out _);

    /// <summary>
    /// The same, reporting which keys were removed.
    ///
    /// The names come from the removal itself rather than from a second pass that reproduces the
    /// rule. Two implementations of "is this declared" are two things to keep in step, and the one
    /// that gets read — a response header, a validation report — is not the one that decides what
    /// is stored, so a divergence would show the caller a set that was never what happened.
    /// </summary>
    public int Sanitize(GlobalTelemetryPayload payload, out IReadOnlyList<string> dropped)
    {
        dropped = [];

        Dictionary<string, string>? dimensions = payload.Dimensions;
        if (dimensions is null || dimensions.Count == 0)
        {
            return 0;
        }

        List<string>? undeclared = null;

        foreach (string key in dimensions.Keys)
        {
            if (!_registry.IsActive(key))
            {
                (undeclared ??= []).Add(key);
            }
        }

        if (undeclared is null)
        {
            return 0;
        }

        for (int i = 0; i < undeclared.Count; i++)
        {
            dimensions.Remove(undeclared[i]);

            TelemetryDiagnostics.UnknownDimensionsDropped.Add(
                1,
                new KeyValuePair<string, object?>("dimension", undeclared[i]),
                new KeyValuePair<string, object?>("project_id", payload.ProjectId));
        }

        LogUndeclaredDimensions(
            _logger,
            undeclared.Count,
            payload.EventType,
            payload.ProjectId,
            string.Join(", ", undeclared.GetRange(0, System.Math.Min(undeclared.Count, MaxNamesPerLogLine))));

        dropped = undeclared;
        return undeclared.Count;
    }
}

namespace Nealytics.Engine.Features.IngestTelemetry;

using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Serialization;

public sealed partial class MeasureSanitizer
{
    private const int MaxNamesPerLogLine = 8;

    private readonly MeasureRegistry _registry;
    private readonly ILogger<MeasureSanitizer> _logger;

    public MeasureSanitizer(MeasureRegistry registry, ILogger<MeasureSanitizer> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    [LoggerMessage(EventId = 9202, Level = LogLevel.Warning,
        Message = "Dropped {DroppedCount} undeclared measure(s) from a {EventType} event on project "
            + "'{ProjectId}': {Names}. Declare them under TelemetryEngine:Measures or the values "
            + "will keep being discarded.")]
    private static partial void LogUndeclaredMeasures(
        ILogger logger, int droppedCount, string eventType, string projectId, string names);

    public int Sanitize(GlobalTelemetryPayload payload) => Sanitize(payload, out _);

    public int Sanitize(GlobalTelemetryPayload payload, out IReadOnlyList<string> dropped)
    {
        dropped = [];

        Dictionary<string, string>? measures = payload.Measures;
        if (measures is null || measures.Count == 0)
        {
            return 0;
        }

        List<string>? undeclared = null;

        foreach (string key in measures.Keys)
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
            measures.Remove(undeclared[i]);

            TelemetryDiagnostics.UnknownMeasuresDropped.Add(
                1,
                new KeyValuePair<string, object?>("measure", undeclared[i]),
                new KeyValuePair<string, object?>("project_id", payload.ProjectId));
        }

        LogUndeclaredMeasures(
            _logger,
            undeclared.Count,
            payload.EventType,
            payload.ProjectId,
            string.Join(", ", undeclared.GetRange(0, System.Math.Min(undeclared.Count, MaxNamesPerLogLine))));

        dropped = undeclared;
        return undeclared.Count;
    }
}

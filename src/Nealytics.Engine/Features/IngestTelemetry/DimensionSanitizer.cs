namespace Nealytics.Engine.Features.IngestTelemetry;

using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Infrastructure.Serialization;

public sealed partial class DimensionSanitizer
{
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

    public int Sanitize(GlobalTelemetryPayload payload) => Sanitize(payload, out _);

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

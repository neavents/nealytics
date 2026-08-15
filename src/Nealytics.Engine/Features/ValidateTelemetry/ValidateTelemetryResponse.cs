namespace Nealytics.Engine.Features.ValidateTelemetry;

using System;
using System.Collections.Generic;

public sealed class ValidateTelemetryResponse
{
    public bool Accepted { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyList<string> DroppedDimensions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DroppedMeasures { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RejectedValues { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();

    public Dictionary<string, string>? StoredDimensions { get; init; }

    public Dictionary<string, string>? StoredMeasures { get; init; }
}

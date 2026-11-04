namespace Nealytics.Engine.Features.ImportJobs;

using System;
using Nealytics.Engine.Infrastructure.Configuration;

internal sealed record ImportJobLimits(int Jobs, int ChunkBytes)
{
    public static ImportJobLimits Create(TelemetryEngineOptions options)
    {
        if (!options.EnableNdjsonImport || options.ImportJobMaxJobs is < 1 or > 4_096
            || options.MaxRequestBodyBytes < 1)
        {
            throw new InvalidOperationException("Import jobs require NDJSON import, 1–4096 jobs and a positive body limit.");
        }
        return new(options.ImportJobMaxJobs, Math.Min(options.MaxRequestBodyBytes, 1_048_576));
    }
}

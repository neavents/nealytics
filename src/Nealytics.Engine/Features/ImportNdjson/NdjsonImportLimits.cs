namespace Nealytics.Engine.Features.ImportNdjson;

using System;
using Nealytics.Engine.Infrastructure.Configuration;

internal sealed record NdjsonImportLimits(int LineBytes, int Records, int Concurrency)
{
    internal static NdjsonImportLimits Create(TelemetryEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ImportMaxLineBytes < 1 || options.ImportMaxLineBytes > 1_048_576
            || options.ImportMaxLineBytes > options.MaxRequestBodyBytes)
        {
            throw new InvalidOperationException("ImportMaxLineBytes must fit 1–1048576 and MaxRequestBodyBytes.");
        }
        if (options.ImportMaxRecords is < 1 or > 10_000)
        {
            throw new InvalidOperationException("ImportMaxRecords must be between 1 and 10000.");
        }
        if (options.ImportMaxConcurrency is < 1 or > 64)
        {
            throw new InvalidOperationException("ImportMaxConcurrency must be between 1 and 64.");
        }
        return new(options.ImportMaxLineBytes, options.ImportMaxRecords, options.ImportMaxConcurrency);
    }
}

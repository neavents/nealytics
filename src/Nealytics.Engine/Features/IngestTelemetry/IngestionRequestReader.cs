namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;

internal sealed class IngestionRequestReader
{
    private readonly int _limit;

    public IngestionRequestReader(IOptions<TelemetryEngineOptions> options)
    {
        TelemetryEngineOptions configuration = options.Value;
        UsesCustomPath = configuration.IngestionExecutionPath?.ToLowerInvariant() switch
        {
            "managed" => false,
            "custom" => true,
            _ => throw new ArgumentException("Ingestion execution path must be managed or custom.", nameof(options))
        };
        ArgumentOutOfRangeException.ThrowIfLessThan(configuration.MaxRequestBodyBytes, 1);
        _limit = configuration.MaxRequestBodyBytes;
    }

    public bool UsesCustomPath { get; }

    public PipeReader Open(HttpContext context) => UsesCustomPath
        ? new BoundedRequestPipeReader(context.Request.BodyReader, _limit)
        : PipeReader.Create(new LengthLimitedStream(context.Request.Body, _limit));
}

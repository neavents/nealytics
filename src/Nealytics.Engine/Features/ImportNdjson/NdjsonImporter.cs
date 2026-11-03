namespace Nealytics.Engine.Features.ImportNdjson;

using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Security;

internal sealed class NdjsonImporter
{
    private readonly DurableIngestDispatcher _dispatcher;
    private readonly IngestGate _gate;
    private readonly DimensionSanitizer _dimensions;
    private readonly MeasureSanitizer _measures;
    private readonly NdjsonImportLimits _limits;
    private readonly IHostApplicationLifetime _lifetime;

    public NdjsonImporter(
        DurableIngestDispatcher dispatcher, IngestGate gate, DimensionSanitizer dimensions,
        MeasureSanitizer measures, NdjsonImportLimits limits, IHostApplicationLifetime lifetime)
    {
        _dispatcher = dispatcher;
        _gate = gate;
        _dimensions = dimensions;
        _measures = measures;
        _limits = limits;
        _lifetime = lifetime;
    }

    internal async Task<NdjsonImportResponse> IngestAsync(
        PipeReader reader, IngestionKeyPolicy policy, DateTime receivedAt, CancellationToken cancellationToken)
    {
        int accepted = 0;
        long offset = 0;
        try
        {
            await foreach (NdjsonRecord record in NdjsonRecordReader.ReadAsync(reader, _limits.LineBytes, cancellationToken))
            {
                if (accepted == _limits.Records)
                {
                    return new(accepted, offset, "record_limit");
                }
                if (record.Error is not null)
                {
                    return new(accepted, offset, record.Error);
                }
                IngestRejection rejection = _gate.Check(policy, record.Payload, receivedAt, out _);
                if (rejection != IngestRejection.None)
                {
                    return new(accepted, offset, IngestValidation.Tag(rejection));
                }
                cancellationToken.ThrowIfCancellationRequested();
                _dimensions.Sanitize(record.Payload!);
                _measures.Sanitize(record.Payload!);
                await _dispatcher.AppendAsync(record.Payload!, _lifetime.ApplicationStopping);
                accepted++;
                offset += record.WireBytes;
            }
            return new(accepted, offset, null);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return new(accepted, offset, "body_too_large");
        }
    }
}

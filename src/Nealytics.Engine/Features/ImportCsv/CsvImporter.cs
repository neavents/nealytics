namespace Nealytics.Engine.Features.ImportCsv;

using System;
using System.Collections.Frozen;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;

internal sealed class CsvImporter(
    DurableIngestDispatcher dispatcher, IngestGate gate, DimensionSanitizer dimensions,
    MeasureSanitizer measures, NdjsonImportLimits limits, IHostApplicationLifetime lifetime)
{
    public async Task<CsvImportResponse> IngestAsync(
        PipeReader reader, CsvProjectMapping mapping, IngestionKeyPolicy policy,
        DateTime receivedAt, CancellationToken cancellationToken)
    {
        int accepted = 0;
        long offset = 0;
        FrozenDictionary<string, int>? columns = null;
        try
        {
            await foreach (CsvRecord record in CsvRecordReader.ReadAsync(reader, limits.LineBytes, cancellationToken))
            {
                if (columns is not null && accepted == limits.Records)
                {
                    return new(accepted, offset, "record_limit");
                }
                if (record.Error is not null)
                {
                    return new(accepted, offset, record.Error);
                }
                if (columns is null)
                {
                    columns = mapping.BindHeader(record.Cells!);
                    if (columns is null)
                    {
                        return new(accepted, offset, "invalid_header");
                    }
                    offset = record.WireBytes;
                    continue;
                }
                if (record.Cells!.Length != columns.Count)
                {
                    return new(accepted, offset, "column_count");
                }
                GlobalTelemetryPayload payload;
                try
                {
                    payload = mapping.Read(columns, record.Cells, limits.LineBytes);
                }
                catch (InvalidDataException)
                {
                    return new(accepted, offset, "invalid_mapping");
                }
                IngestRejection rejection = gate.Check(policy, payload, receivedAt, out _);
                if (rejection != IngestRejection.None)
                {
                    return new(accepted, offset, IngestValidation.Tag(rejection));
                }
                cancellationToken.ThrowIfCancellationRequested();
                dimensions.Sanitize(payload);
                measures.Sanitize(payload);
                await dispatcher.AppendAsync(payload, lifetime.ApplicationStopping);
                accepted++;
                offset += record.WireBytes;
            }
            return new(accepted, offset, columns is null ? "header_required" : null);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return new(accepted, offset, "body_too_large");
        }
    }
}

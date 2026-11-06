namespace Nealytics.Engine.Features.S3Import;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Infrastructure.Networking;
using Nealytics.Engine.Infrastructure.Storage.Wal;

internal sealed class S3ObjectClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ImportJobLimits _limits;
    private readonly NdjsonImportLimits _input;
    private readonly Func<DateTimeOffset> _now;
    private readonly ArrayPool<byte> _pool;

    public S3ObjectClient(ImportJobLimits limits, NdjsonImportLimits input)
        : this(limits, input, CreateHandler())
    {
    }

    internal S3ObjectClient(ImportJobLimits limits, NdjsonImportLimits input, HttpMessageHandler handler,
        Func<DateTimeOffset>? now = null, ArrayPool<byte>? pool = null)
    {
        _limits = limits;
        _input = input;
        _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    internal static SocketsHttpHandler CreateHandler() => ObjectSourceTransport.CreateHandler();

    public Task<S3ObjectSnapshot> InspectAsync(S3Source source, S3ObjectRequest descriptor, CancellationToken cancellationToken)
    {
        if (!S3Sources.Key(source, descriptor.Key) || !S3Sources.Version(descriptor.VersionId))
        {
            throw new S3ImportException(400, "invalid_object_request");
        }
        HttpRequestMessage request = new(HttpMethod.Head, S3ObjectAddress.Create(source, descriptor.Key, descriptor.VersionId));
        return FetchAsync(source, request, (response, _) =>
        {
            RequireStatus(response, HttpStatusCode.OK);
            string? tag = Header(response, "ETag");
            string? version = Header(response, "x-amz-version-id");
            long? bytes = response.Content.Headers.ContentLength;
            RequireEncoding(response);
            if (!S3ObjectSnapshot.IsETag(tag) || !S3Sources.Version(version) || bytes is null or < 0)
            {
                throw new S3ImportException(502, "source_protocol_error");
            }
            if (bytes > source.MaxObjectBytes)
            {
                throw new S3ImportException(413, "source_too_large");
            }
            if (descriptor.VersionId is not null && descriptor.VersionId != version)
            {
                throw new S3ImportException(409, "source_changed");
            }
            return Task.FromResult(new S3ObjectSnapshot(descriptor.Key, version, tag!, bytes.Value,
                S3ObjectSnapshot.Identity(source, descriptor.Key, version, tag!, bytes.Value)));
        }, cancellationToken);
    }

    public async Task<ObjectSourceChunk> ReadChunkAsync(S3Source source, S3ObjectSnapshot snapshot, long offset,
        int? exactLength, CancellationToken cancellationToken)
    {
        if (!snapshot.IsValid(source) || offset < 0 || offset > snapshot.Bytes
            || (offset == snapshot.Bytes && snapshot.Bytes != 0)
            || (exactLength is int requested && (requested < 0 || requested > _limits.ChunkBytes
                || requested > snapshot.Bytes - offset || (requested == 0 && snapshot.Bytes != 0))))
        {
            throw new S3ImportException(400, "invalid_object_request");
        }
        int count = exactLength ?? (int)Math.Min(_limits.ChunkBytes, snapshot.Bytes - offset);
        HttpRequestMessage request = new(count == 0 ? HttpMethod.Head : HttpMethod.Get,
            S3ObjectAddress.Create(source, snapshot.Key, snapshot.VersionId));
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(snapshot.ETag));
        if (count != 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
        }
        WalRecordBuffer buffer = new(_limits.ChunkBytes, _pool);
        try
        {
            int length = await FetchAsync(source, request, async (response, token) =>
            {
                RequireStatus(response, count == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent);
                RequireRepresentation(response, snapshot);
                if (response.Content.Headers.ContentLength != count)
                {
                    throw new S3ImportException(502, "source_protocol_error");
                }
                if (count == 0)
                {
                    return 0;
                }
                ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
                if (range is null || range.Unit != "bytes" || range.From != offset
                    || range.To != offset + count - 1 || range.Length != snapshot.Bytes)
                {
                    throw new S3ImportException(502, "source_protocol_error");
                }
                Stream body = await response.Content.ReadAsStreamAsync(token);
                if (!await ObjectSourceTransport.ReadExactlyAsync(body, buffer, count, token))
                {
                    throw new S3ImportException(502, "source_protocol_error");
                }
                int framed = exactLength ?? NdjsonChunkBoundary.Length(buffer.WrittenMemory.Span,
                    offset + count == snapshot.Bytes, _input.Records);
                if (framed == 0)
                {
                    throw new S3ImportException(413, "source_record_too_large");
                }
                return framed;
            }, cancellationToken);
            return new(buffer, length);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private async Task<T> FetchAsync<T>(S3Source source, HttpRequestMessage request,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        using (request)
        using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(source.Timeout);
            try
            {
                S3RequestSigner.Sign(request, source, _now());
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                return await read(response, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new S3ImportException(504, "source_timeout");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                throw new S3ImportException(502, "source_unavailable");
            }
        }
    }

    private static void RequireRepresentation(HttpResponseMessage response, S3ObjectSnapshot snapshot)
    {
        RequireEncoding(response);
        if (Header(response, "ETag") != snapshot.ETag || Header(response, "x-amz-version-id") != snapshot.VersionId)
        {
            throw new S3ImportException(409, "source_changed");
        }
    }

    private static void RequireEncoding(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new S3ImportException(415, "source_encoding_unsupported");
        }
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out IEnumerable<string>? values))
        {
            return null;
        }
        string[] entries = values.ToArray();
        if (entries.Length != 1)
        {
            throw new S3ImportException(502, "source_protocol_error");
        }
        return entries[0];
    }

    private static void RequireStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode == expected)
        {
            return;
        }
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new S3ImportException(404, "source_object_not_found"),
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new S3ImportException(502, "source_access_denied"),
            HttpStatusCode.PreconditionFailed or HttpStatusCode.RequestedRangeNotSatisfiable => new S3ImportException(409, "source_changed"),
            >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => new S3ImportException(502, "source_protocol_error"),
            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => new S3ImportException(502, "source_redirect_refused"),
            _ => new S3ImportException(502, "source_unavailable")
        };
    }

    public void Dispose() => _http.Dispose();
}

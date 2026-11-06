namespace Nealytics.Engine.Features.GcsImport;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
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

internal sealed class GcsObjectClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ImportJobLimits _limits;
    private readonly NdjsonImportLimits _input;
    private readonly Func<DateTimeOffset> _now;
    private readonly ArrayPool<byte> _pool;

    public GcsObjectClient(ImportJobLimits limits, NdjsonImportLimits input)
        : this(limits, input, CreateHandler())
    {
    }

    internal GcsObjectClient(ImportJobLimits limits, NdjsonImportLimits input, HttpMessageHandler handler,
        Func<DateTimeOffset>? now = null, ArrayPool<byte>? pool = null)
    {
        _limits = limits;
        _input = input;
        _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    internal static SocketsHttpHandler CreateHandler() => ObjectSourceTransport.CreateHandler();

    public Task<GcsObjectSnapshot> InspectAsync(GcsSource source, GcsObjectRequest descriptor, CancellationToken cancellationToken)
    {
        if (!GcsSources.Key(source, descriptor.Key) || descriptor.Generation is <= 0)
        {
            throw new GcsImportException(400, "invalid_object_request");
        }
        HttpRequestMessage request = Request(source, HttpMethod.Head, descriptor.Key, descriptor.Generation);
        return FetchAsync(source, request, (response, _) =>
        {
            RequireStatus(response, HttpStatusCode.OK);
            ObjectMetadata metadata = Metadata(response);
            if (response.Content.Headers.ContentLength != metadata.Bytes)
            {
                throw new GcsImportException(502, "source_protocol_error");
            }
            if (metadata.Bytes > source.MaxObjectBytes)
            {
                throw new GcsImportException(413, "source_too_large");
            }
            if (descriptor.Generation is not null && descriptor.Generation != metadata.Generation)
            {
                throw new GcsImportException(409, "source_changed");
            }
            return Task.FromResult(new GcsObjectSnapshot(descriptor.Key, metadata.Generation, metadata.Metageneration,
                metadata.ETag, metadata.Bytes, source.Identity(descriptor.Key, metadata.Generation,
                    metadata.Metageneration, metadata.ETag, metadata.Bytes)));
        }, cancellationToken);
    }

    public async Task<ObjectSourceChunk> ReadChunkAsync(GcsSource source, GcsObjectSnapshot snapshot, long offset,
        int? exactLength, CancellationToken cancellationToken)
    {
        if (!snapshot.IsValid(source) || offset < 0 || offset > snapshot.Bytes
            || (offset == snapshot.Bytes && snapshot.Bytes != 0)
            || (exactLength is int requested && (requested < 0 || requested > _limits.ChunkBytes
                || requested > snapshot.Bytes - offset || (requested == 0 && snapshot.Bytes != 0))))
        {
            throw new GcsImportException(400, "invalid_object_request");
        }
        int count = exactLength ?? (int)Math.Min(_limits.ChunkBytes, snapshot.Bytes - offset);
        HttpRequestMessage request = Request(source, count == 0 ? HttpMethod.Head : HttpMethod.Get,
            snapshot.Key, snapshot.Generation);
        request.Headers.Add("x-goog-if-generation-match", snapshot.Generation.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("x-goog-if-metageneration-match", snapshot.Metageneration.ToString(CultureInfo.InvariantCulture));
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
                    throw new GcsImportException(502, "source_protocol_error");
                }
                if (count == 0)
                {
                    return 0;
                }
                ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
                if (range is null || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || range.From != offset
                    || range.To != offset + count - 1 || range.Length != snapshot.Bytes)
                {
                    throw new GcsImportException(502, "source_protocol_error");
                }
                Stream body = await response.Content.ReadAsStreamAsync(token);
                if (!await ObjectSourceTransport.ReadExactlyAsync(body, buffer, count, token))
                {
                    throw new GcsImportException(502, "source_protocol_error");
                }
                int framed = exactLength ?? NdjsonChunkBoundary.Length(buffer.WrittenMemory.Span,
                    offset + count == snapshot.Bytes, _input.Records);
                if (framed == 0)
                {
                    throw new GcsImportException(413, "source_record_too_large");
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

    private async Task<T> FetchAsync<T>(GcsSource source, HttpRequestMessage request,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        using (request)
        using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(source.Timeout);
            try
            {
                AwsV4RequestSigner.Sign(request, source.AccessKey, source.SecretKey, "auto", "", _now());
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                return await read(response, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GcsImportException(504, "source_timeout");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                throw new GcsImportException(502, "source_unavailable");
            }
        }
    }

    private static void RequireRepresentation(HttpResponseMessage response, GcsObjectSnapshot snapshot)
    {
        ObjectMetadata metadata = Metadata(response);
        if (metadata.ETag != snapshot.ETag || metadata.Generation != snapshot.Generation
            || metadata.Metageneration != snapshot.Metageneration || metadata.Bytes != snapshot.Bytes)
        {
            throw new GcsImportException(409, "source_changed");
        }
    }

    private static ObjectMetadata Metadata(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase))
            || !string.Equals(Header(response, "x-goog-stored-content-encoding"), "identity", StringComparison.OrdinalIgnoreCase))
        {
            throw new GcsImportException(415, "source_encoding_unsupported");
        }
        string? tag = Header(response, "ETag");
        if (!GcsObjectSnapshot.IsETag(tag))
        {
            throw new GcsImportException(502, "source_protocol_error");
        }
        return new(tag!, Number(response, "x-goog-generation", true),
            Number(response, "x-goog-metageneration", true), Number(response, "x-goog-stored-content-length", false));
    }

    private static long Number(HttpResponseMessage response, string name, bool positive)
    {
        if (!long.TryParse(Header(response, name), NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            || value < (positive ? 1 : 0))
        {
            throw new GcsImportException(502, "source_protocol_error");
        }
        return value;
    }

    private static HttpRequestMessage Request(GcsSource source, HttpMethod method, string key, long? generation)
    {
        HttpRequestMessage request = new(method, GcsObjectAddress.Create(source, key, generation));
        request.Headers.AcceptEncoding.ParseAdd("identity");
        return request;
    }

    private readonly record struct ObjectMetadata(string ETag, long Generation, long Metageneration, long Bytes);

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out IEnumerable<string>? values))
        {
            return null;
        }
        string[] entries = values.ToArray();
        if (entries.Length != 1)
        {
            throw new GcsImportException(502, "source_protocol_error");
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
            HttpStatusCode.NotFound => new GcsImportException(404, "source_object_not_found"),
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new GcsImportException(502, "source_access_denied"),
            HttpStatusCode.PreconditionFailed or HttpStatusCode.RequestedRangeNotSatisfiable => new GcsImportException(409, "source_changed"),
            >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => new GcsImportException(502, "source_protocol_error"),
            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => new GcsImportException(502, "source_redirect_refused"),
            _ => new GcsImportException(502, "source_unavailable")
        };
    }

    public void Dispose() => _http.Dispose();
}

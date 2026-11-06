namespace Nealytics.Engine.Features.HttpImport;

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

internal sealed class HttpObjectClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ImportJobLimits _limits;
    private readonly NdjsonImportLimits _input;
    private readonly ArrayPool<byte> _pool;

    public HttpObjectClient(ImportJobLimits limits, NdjsonImportLimits input)
        : this(limits, input, CreateHandler())
    {
    }

    internal HttpObjectClient(ImportJobLimits limits, NdjsonImportLimits input, HttpMessageHandler handler,
        ArrayPool<byte>? pool = null)
    {
        _limits = limits;
        _input = input;
        _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    internal static SocketsHttpHandler CreateHandler() => ObjectSourceTransport.CreateHandler();

    public Task<HttpObjectSnapshot> InspectAsync(HttpSource source, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = Request(source, HttpMethod.Head);
        return FetchAsync(source, request, (response, _) =>
        {
            RequireStatus(response, HttpStatusCode.OK);
            string? tag = Header(response, "ETag");
            long? bytes = response.Content.Headers.ContentLength;
            RequireEncoding(response);
            if (!HttpObjectSnapshot.IsETag(tag) || bytes is null or < 0)
            {
                throw new HttpImportException(502, "source_protocol_error");
            }
            if (bytes > source.MaxObjectBytes)
            {
                throw new HttpImportException(413, "source_too_large");
            }
            return Task.FromResult(new HttpObjectSnapshot(tag!, bytes.Value, source.Identity(tag!, bytes.Value)));
        }, cancellationToken);
    }

    public async Task<ObjectSourceChunk> ReadChunkAsync(HttpSource source, HttpObjectSnapshot snapshot, long offset,
        int? exactLength, CancellationToken cancellationToken)
    {
        if (!snapshot.IsValid(source) || offset < 0 || offset > snapshot.Bytes
            || (offset == snapshot.Bytes && snapshot.Bytes != 0)
            || (exactLength is int requested && (requested < 0 || requested > _limits.ChunkBytes
                || requested > snapshot.Bytes - offset || (requested == 0 && snapshot.Bytes != 0))))
        {
            throw new HttpImportException(400, "invalid_object_request");
        }
        int count = exactLength ?? (int)Math.Min(_limits.ChunkBytes, snapshot.Bytes - offset);
        HttpRequestMessage request = Request(source, count == 0 ? HttpMethod.Head : HttpMethod.Get);
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
                    throw new HttpImportException(502, "source_protocol_error");
                }
                if (count == 0)
                {
                    return 0;
                }
                ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
                if (range is null || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || range.From != offset
                    || range.To != offset + count - 1 || range.Length != snapshot.Bytes)
                {
                    throw new HttpImportException(502, "source_protocol_error");
                }
                Stream body = await response.Content.ReadAsStreamAsync(token);
                if (!await ObjectSourceTransport.ReadExactlyAsync(body, buffer, count, token))
                {
                    throw new HttpImportException(502, "source_protocol_error");
                }
                int framed = exactLength ?? NdjsonChunkBoundary.Length(buffer.WrittenMemory.Span,
                    offset + count == snapshot.Bytes, _input.Records);
                if (framed == 0)
                {
                    throw new HttpImportException(413, "source_record_too_large");
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

    private async Task<T> FetchAsync<T>(HttpSource source, HttpRequestMessage request,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        using (request)
        using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(source.Timeout);
            try
            {
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                return await read(response, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpImportException(504, "source_timeout");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                throw new HttpImportException(502, "source_unavailable");
            }
        }
    }

    private static void RequireRepresentation(HttpResponseMessage response, HttpObjectSnapshot snapshot)
    {
        RequireEncoding(response);
        if (Header(response, "ETag") != snapshot.ETag)
        {
            throw new HttpImportException(409, "source_changed");
        }
    }

    private static void RequireEncoding(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new HttpImportException(415, "source_encoding_unsupported");
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
            throw new HttpImportException(502, "source_protocol_error");
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
            HttpStatusCode.NotFound => new HttpImportException(404, "source_object_not_found"),
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new HttpImportException(502, "source_access_denied"),
            HttpStatusCode.PreconditionFailed or HttpStatusCode.RequestedRangeNotSatisfiable => new HttpImportException(409, "source_changed"),
            >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => new HttpImportException(502, "source_protocol_error"),
            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => new HttpImportException(502, "source_redirect_refused"),
            _ => new HttpImportException(502, "source_unavailable")
        };
    }

    private static HttpRequestMessage Request(HttpSource source, HttpMethod method)
    {
        HttpRequestMessage request = new(method, source.Url);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        foreach (KeyValuePair<string, string> header in source.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return request;
    }

    public void Dispose() => _http.Dispose();
}

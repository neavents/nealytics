using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Features.S3Import;

namespace Nealytics.Engine.Tests.Unit;

internal static class S3TransportTestSupport
{
    internal static S3ObjectSnapshot Snapshot(S3Source source, long size, string? version = "v") =>
        new("prefix/file.ndjson", version, "\"tag\"", size,
            S3ObjectSnapshot.Identity(source, "prefix/file.ndjson", version, "\"tag\"", size));

    internal static S3ObjectClient Client(Handler handler, int bytes = 16, int records = 2,
        System.Buffers.ArrayPool<byte>? pool = null) =>
        new(new ImportJobLimits(8, bytes), new NdjsonImportLimits(bytes, records, 1), handler,
            () => new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero), pool);

    internal static HttpResponseMessage Response(string body = "", long? total = null, long offset = 0,
        string? version = "v", TrackingStream? stream = null)
    {
        var response = new HttpResponseMessage(total is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = stream is null ? new ByteArrayContent(Encoding.UTF8.GetBytes(body)) : new StreamContent(stream)
        };
        response.Headers.TryAddWithoutValidation("ETag", "\"tag\"");
        if (version is not null) response.Headers.TryAddWithoutValidation("x-amz-version-id", version);
        int length = Encoding.UTF8.GetByteCount(body);
        response.Content.Headers.ContentLength = length;
        if (total is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, total.Value);
        return response;
    }

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public bool Disposed { get; private set; }
        internal Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : this((request, _) => Task.FromResult(send(request))) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return send(request, cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    internal sealed class TrackingStream(byte[] bytes, int fragment = int.MaxValue) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public int Reads { get; private set; }
        private bool _eof;
        public IOException? Failure { get; init; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (_eof) throw new InvalidOperationException("must not reread terminal EOF");
            if (Failure is not null && Reads > 1) throw Failure;
            int count = await base.ReadAsync(buffer[..Math.Min(fragment, buffer.Length)], cancellationToken);
            _eof = count == 0;
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    internal sealed class UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
    }
}

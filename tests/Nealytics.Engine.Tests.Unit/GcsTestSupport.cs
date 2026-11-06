using System.Net;
using System.Text;
using Nealytics.Engine.Features.GcsImport;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Infrastructure.Configuration;
using Handler = Nealytics.Engine.Tests.Unit.S3TransportTestSupport.Handler;
using TrackingStream = Nealytics.Engine.Tests.Unit.S3TransportTestSupport.TrackingStream;

namespace Nealytics.Engine.Tests.Unit;

internal static class GcsTestSupport
{
    internal static GcsSourceOptions Options() => new()
    {
        Name = "source", ProjectId = "p", Bucket = "private-bucket", AccessKey = "GOOGTESTACCESS",
        SecretKey = "private-secret+/=", IdentityKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
    };

    internal static GcsSources Catalog(GcsSourceOptions? source = null, Action<TelemetryEngineOptions>? customize = null)
    {
        var options = new TelemetryEngineOptions { EnableNdjsonImport = true, EnableImportJobs = true, GcsSources = [source ?? Options()] };
        customize?.Invoke(options);
        return new(options, new ProjectCatalog(options, ProjectContext.Compile("baseline", options)));
    }

    internal static GcsSource Source() => Catalog().Find("source", "p")!;

    internal static GcsObjectClient Client(Handler handler, int bytes = 16, int records = 2,
        System.Buffers.ArrayPool<byte>? pool = null) =>
        new(new ImportJobLimits(8, bytes), new NdjsonImportLimits(bytes, records, 1), handler,
            () => new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero), pool);

    internal static GcsObjectSnapshot Snapshot(GcsSource source, long bytes, string key = "prefix/file.ndjson",
        long generation = 17, long metageneration = 3, string tag = "\"tag\"") =>
        new(key, generation, metageneration, tag, bytes, source.Identity(key, generation, metageneration, tag, bytes));

    internal static HttpResponseMessage Response(string body = "", long? total = null, long offset = 0,
        TrackingStream? stream = null)
    {
        var response = S3TransportTestSupport.Response(body, total, offset, version: null, stream: stream);
        response.Headers.Add("x-goog-generation", "17"); response.Headers.Add("x-goog-metageneration", "3");
        response.Headers.Add("x-goog-stored-content-encoding", "identity");
        response.Headers.Add("x-goog-stored-content-length", (total ?? Encoding.UTF8.GetByteCount(body)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    internal static HttpResponseMessage Metadata(long bytes)
    {
        var response = Response(); response.Content.Headers.ContentLength = bytes;
        response.Headers.Remove("x-goog-stored-content-length");
        response.Headers.Add("x-goog-stored-content-length", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }
}

using Nealytics.Engine.Features.HttpImport;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Infrastructure.Configuration;
using static Nealytics.Engine.Tests.Unit.S3TransportTestSupport;

namespace Nealytics.Engine.Tests.Unit;

internal static class HttpObjectTestSupport
{
    internal static HttpSourceOptions Options() => new()
    {
        Name = "source", ProjectId = "p", Url = "https://origin.example/file.ndjson",
        Headers = new() { ["Authorization"] = "Bearer private" },
        IdentityKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
    };

    internal static HttpSources Catalog(HttpSourceOptions? source = null, Action<TelemetryEngineOptions>? customize = null)
    {
        var options = new TelemetryEngineOptions { EnableNdjsonImport = true, EnableImportJobs = true, HttpSources = [source ?? Options()] };
        customize?.Invoke(options);
        return new(options, new ProjectCatalog(options, ProjectContext.Compile("baseline", options)));
    }

    internal static HttpObjectClient Client(Handler handler, int bytes = 16, int records = 2,
        System.Buffers.ArrayPool<byte>? pool = null) =>
        new(new ImportJobLimits(8, bytes), new NdjsonImportLimits(bytes, records, 1), handler, pool);

    internal static HttpObjectSnapshot Snapshot(HttpSource source, long bytes, string tag = "\"tag\"") =>
        new(tag, bytes, source.Identity(tag, bytes));
}

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Features.GcsImport;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;

namespace Nealytics.Engine.Tests.Unit;

public sealed class GcsObjectWireTests
{
    [Fact]
    public async Task RealHttpPreservesLiteralNamesAndPinsGenerationPreconditionsWithoutCookiesOrRedirects()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        var observed = new List<(string Method, string Target, string Cookie, string Generation, string Metadata, string Auth)>();
        bool redirect = false;
        server.Run(async context =>
        {
            observed.Add((context.Request.Method, context.Features.Get<IHttpRequestFeature>()!.RawTarget,
                context.Request.Headers.Cookie.ToString(), context.Request.Headers["x-goog-if-generation-match"].ToString(),
                context.Request.Headers["x-goog-if-metageneration-match"].ToString(), context.Request.Headers.Authorization.ToString()));
            if (redirect) { context.Response.StatusCode = 307; context.Response.Headers.Location = "/elsewhere"; return; }
            Headers(context); context.Response.Headers.SetCookie = "private=one; Path=/";
            context.Response.ContentLength = 4;
            if (context.Request.Method == "GET")
            {
                Assert.Equal("bytes=0-3", context.Request.Headers.Range.ToString());
                Assert.Equal("\"tag\"", context.Request.Headers.IfMatch.ToString());
                context.Response.StatusCode = 206; context.Response.Headers.ContentRange = "bytes 0-3/4";
                await context.Response.Body.WriteAsync("a\nb\n"u8.ToArray());
            }
        });
        await server.StartAsync();
        try
        {
            var options = GcsTestSupport.Options(); options.Endpoint = Address(server); options.AllowInsecureHttp = true;
            using var catalog = GcsTestSupport.Catalog(options); var source = catalog.Find("source", "p")!;
            using var client = new GcsObjectClient(new ImportJobLimits(8, 16), new NdjsonImportLimits(16, 2, 1));
            string key = "prefix/a//./../%/日本語 +?#";
            var snapshot = await client.InspectAsync(source, new(key, null), default);
            using var chunk = await client.ReadChunkAsync(source, snapshot, 0, null, default);
            Assert.Equal("a\nb\n", Encoding.UTF8.GetString(chunk.Bytes.Span));
            Assert.Equal(key, snapshot.Key); Assert.Equal(17, snapshot.Generation); Assert.Equal(3, snapshot.Metageneration);
            Assert.Equal(2, observed.Count); Assert.Equal("HEAD", observed[0].Method); Assert.Equal("GET", observed[1].Method);
            string path = "/private-bucket/prefix/a//./../%25/%E6%97%A5%E6%9C%AC%E8%AA%9E%20%2B%3F%23";
            Assert.Equal(path, observed[0].Target); Assert.Equal(path + "?generation=17", observed[1].Target);
            Assert.All(observed, row => { Assert.Empty(row.Cookie); Assert.StartsWith("AWS4-HMAC-SHA256 Credential=GOOGTESTACCESS/", row.Auth); });
            Assert.Empty(observed[0].Generation); Assert.Empty(observed[0].Metadata);
            Assert.Equal("17", observed[1].Generation); Assert.Equal("3", observed[1].Metadata);
            redirect = true;
            var error = await Assert.ThrowsAsync<GcsImportException>(() => client.InspectAsync(source, new(key, null), default));
            Assert.Equal("source_redirect_refused", error.Error); Assert.Equal(3, observed.Count);
        }
        finally { await server.StopAsync(); }
    }

    [Theory]
    [InlineData("ignored-range", "source_protocol_error")]
    [InlineData("missing-range", "source_protocol_error")]
    [InlineData("truncated", "source_unavailable")]
    [InlineData("deadline", "source_timeout")]
    [InlineData("transcoded", "source_encoding_unsupported")]
    [InlineData("generation", "source_changed")]
    [InlineData("metageneration", "source_changed")]
    public async Task RealOriginFailuresNeverReturnAnUnverifiedSourceChunk(string fault, string code)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.Run(async context =>
        {
            Headers(context); context.Response.ContentLength = 4;
            context.Response.StatusCode = fault == "ignored-range" ? 200 : 206;
            if (fault != "missing-range") context.Response.Headers.ContentRange = "bytes 0-3/4";
            if (fault == "transcoded") context.Response.Headers["x-goog-stored-content-encoding"] = "gzip";
            if (fault == "generation") context.Response.Headers["x-goog-generation"] = "18";
            if (fault == "metageneration") context.Response.Headers["x-goog-metageneration"] = "4";
            if (fault == "deadline")
            {
                await context.Response.StartAsync();
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
                catch (OperationCanceledException) { }
                return;
            }
            if (fault == "truncated")
            {
                await context.Response.Body.WriteAsync("a"u8.ToArray()); await context.Response.Body.FlushAsync(); context.Abort(); return;
            }
            await context.Response.Body.WriteAsync("a\nb\n"u8.ToArray());
        });
        await server.StartAsync();
        try
        {
            var options = GcsTestSupport.Options(); options.Endpoint = Address(server);
            options.AllowInsecureHttp = true; options.RequestTimeoutSeconds = 1;
            using var catalog = GcsTestSupport.Catalog(options); var source = catalog.Find("source", "p")!;
            using var client = new GcsObjectClient(new ImportJobLimits(8, 16), new NdjsonImportLimits(16, 2, 1));
            using var safeguard = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var error = await Assert.ThrowsAsync<GcsImportException>(() => client.ReadChunkAsync(source,
                GcsTestSupport.Snapshot(source, 4), 0, null, safeguard.Token));
            Assert.Equal(code, error.Error);
            Assert.Equal(fault == "deadline" ? 504 : fault == "transcoded" ? 415 : fault is "generation" or "metageneration" ? 409 : 502, error.StatusCode);
        }
        finally { await server.StopAsync(); }
    }

    private static string Address(WebApplication server) =>
        server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private static void Headers(HttpContext context)
    {
        context.Response.Headers.ETag = "\"tag\"";
        context.Response.Headers["x-goog-generation"] = "17";
        context.Response.Headers["x-goog-metageneration"] = "3";
        context.Response.Headers["x-goog-stored-content-encoding"] = "identity";
        context.Response.Headers["x-goog-stored-content-length"] = "4";
    }
}

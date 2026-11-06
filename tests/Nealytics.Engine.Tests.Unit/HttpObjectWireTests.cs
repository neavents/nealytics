using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Features.HttpImport;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;

namespace Nealytics.Engine.Tests.Unit;

public sealed class HttpObjectWireTests
{
    [Theory]
    [InlineData("ignored-range", "source_protocol_error")]
    [InlineData("missing-range", "source_protocol_error")]
    [InlineData("truncated", "source_unavailable")]
    [InlineData("deadline", "source_timeout")]
    public async Task RealOriginProtocolFailuresAndBodyDeadlinesNeverReturnAChunk(string fault, string code)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapGet("/object", async context =>
        {
            context.Response.StatusCode = fault == "ignored-range" ? 200 : 206;
            context.Response.Headers.ETag = "\"tag\""; context.Response.ContentLength = 4;
            if (fault != "missing-range") context.Response.Headers.ContentRange = "bytes 0-3/4";
            if (fault == "deadline")
            {
                await context.Response.StartAsync();
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
                catch (OperationCanceledException) { }
                return;
            }
            if (fault == "truncated")
            {
                await context.Response.Body.WriteAsync("a"u8.ToArray());
                await context.Response.Body.FlushAsync(); context.Abort(); return;
            }
            await context.Response.Body.WriteAsync("a\nb\n"u8.ToArray());
        });
        await server.StartAsync();
        try
        {
            var options = HttpObjectTestSupport.Options(); options.AllowInsecureHttp = true; options.RequestTimeoutSeconds = 1;
            options.Url = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/object";
            using var catalog = HttpObjectTestSupport.Catalog(options); var source = catalog.Find("source", "p")!;
            using var client = new HttpObjectClient(new ImportJobLimits(8, 16), new NdjsonImportLimits(16, 2, 1));
            using var safeguard = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var error = await Assert.ThrowsAsync<HttpImportException>(() => client.ReadChunkAsync(source,
                HttpObjectTestSupport.Snapshot(source, 4), 0, null, safeguard.Token));
            Assert.Equal(code, error.Error); Assert.Equal(fault == "deadline" ? 504 : 502, error.StatusCode);
        }
        finally { await server.StopAsync(); }
    }

    [Fact]
    public async Task RealHttpUsesOnlyTheConfiguredUrlAndHeadersAndNeverStoresCookiesOrFollowsRedirects()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        var observed = new List<(string Method, string Query, string Auth, string Cookie, string Range, string Match)>();
        byte[] body = "a\nb\n"u8.ToArray();
        server.MapMethods("/export", ["HEAD", "GET"], async context =>
        {
            lock (observed) observed.Add((context.Request.Method, context.Request.Query["selection"].ToString(),
                context.Request.Headers.Authorization.ToString(), context.Request.Headers.Cookie.ToString(),
                context.Request.Headers.Range.ToString(), context.Request.Headers.IfMatch.ToString()));
            context.Response.Headers.ETag = "\"tag\"";
            context.Response.Headers.SetCookie = "private=one; Path=/";
            context.Response.ContentLength = body.Length;
            if (context.Request.Method == "GET")
            {
                context.Response.StatusCode = 206;
                context.Response.Headers.ContentRange = "bytes 0-3/4";
                await context.Response.Body.WriteAsync(body);
            }
        });
        server.MapMethods("/redirect", ["HEAD", "GET"], context =>
        {
            context.Response.StatusCode = 307; context.Response.Headers.Location = "/export"; return Task.CompletedTask;
        });
        await server.StartAsync();
        try
        {
            string address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var options = HttpObjectTestSupport.Options(); options.AllowInsecureHttp = true;
            options.Url = address + "/export?selection=%E6%97%A5%E6%9C%AC%E8%AA%9E%20%2B&secret=private";
            using var sources = HttpObjectTestSupport.Catalog(options);
            var source = sources.Find("source", "p")!;
            using var client = new HttpObjectClient(new ImportJobLimits(8, 16), new NdjsonImportLimits(16, 2, 1));
            var snapshot = await client.InspectAsync(source, default);
            using var chunk = await client.ReadChunkAsync(source, snapshot, 0, null, default);
            Assert.Equal(body, chunk.Bytes.ToArray());
            Assert.Equal(2, observed.Count);
            Assert.Equal("HEAD", observed[0].Method); Assert.Equal("GET", observed[1].Method);
            Assert.All(observed, request =>
            {
                Assert.Equal("日本語 +", request.Query); Assert.Equal("Bearer private", request.Auth);
                Assert.Equal("", request.Cookie);
            });
            Assert.Equal("bytes=0-3", observed[1].Range); Assert.Equal("\"tag\"", observed[1].Match);
            options.Url = address + "/redirect";
            using var redirects = HttpObjectTestSupport.Catalog(options);
            var error = await Assert.ThrowsAsync<HttpImportException>(() => client.InspectAsync(redirects.Find("source", "p")!, default));
            Assert.Equal(502, error.StatusCode); Assert.Equal("source_redirect_refused", error.Error);
            Assert.Equal(2, observed.Count);
        }
        finally { await server.StopAsync(); }
    }
}

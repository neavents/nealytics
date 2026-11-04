using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Features.S3Import;

namespace Nealytics.Engine.Tests.Unit;

public sealed class S3WireTests
{
    [Fact]
    public async Task TheDefaultClientPreservesTheSignedRawTargetAndDoesNotRetainResponseCookies()
    {
        ConcurrentQueue<(string Target, string Host, string Cookie, string Authorization)> requests = new();
        await using var server = await Server.Start(context =>
        {
            requests.Enqueue((context.Features.Get<IHttpRequestFeature>()!.RawTarget,
                context.Request.Host.ToUriComponent(), context.Request.Headers.Cookie.ToString(), context.Request.Headers.Authorization.ToString()));
            context.Response.Headers.ETag = "\"tag\"";
            context.Response.Headers.SetCookie = "source_cookie=private; Path=/";
            context.Response.ContentLength = 0;
            return Task.CompletedTask;
        });
        var options = S3TestSupport.Options(); options.Endpoint = server.Address; options.AllowInsecureHttp = true;
        var source = S3TestSupport.Source(options);
        using var client = new S3ObjectClient(new(4, 64), new(64, 2, 1));
        for (int index = 0; index < 2; index++)
        {
            var snapshot = await client.InspectAsync(source, new("a//./../日本語 %", null), CancellationToken.None);
            Assert.Equal(0, snapshot.Bytes);
        }
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("/examplebucket/a//./../%E6%97%A5%E6%9C%AC%E8%AA%9E%20%25", request.Target);
            Assert.Equal(new Uri(server.Address).Authority, request.Host);
            Assert.Empty(request.Cookie);
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=", request.Authorization);
        });
    }

    [Fact]
    public async Task RedirectsNeverForwardTheSignedRequestToAnotherTarget()
    {
        int requests = 0;
        await using var server = await Server.Start(context =>
        {
            Interlocked.Increment(ref requests);
            context.Response.StatusCode = 307;
            context.Response.Headers.Location = "/redirected";
            return Task.CompletedTask;
        });
        var options = S3TestSupport.Options(); options.Endpoint = server.Address; options.AllowInsecureHttp = true;
        using var client = new S3ObjectClient(new(4, 64), new(64, 2, 1));
        var error = await Assert.ThrowsAsync<S3ImportException>(() => client.InspectAsync(S3TestSupport.Source(options),
            new("file", null), CancellationToken.None));
        Assert.Equal(502, error.StatusCode);
        Assert.Equal("source_redirect_refused", error.Error);
        Assert.Equal(1, requests);
    }

    private sealed class Server(WebApplication app, string address) : IAsyncDisposable
    {
        public string Address { get; } = address;
        public static async Task<Server> Start(RequestDelegate handle)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.Run(handle);
            await app.StartAsync();
            string address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            return new(app, address);
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}

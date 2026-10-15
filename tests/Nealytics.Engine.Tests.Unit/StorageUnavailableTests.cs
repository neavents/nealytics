using System.Net.Sockets;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;
using Octonica.ClickHouseClient.Exceptions;

namespace Nealytics.Engine.Tests.Unit;

public class StorageUnavailableTests
{
    private static ClickHouseServerException Server(int code) =>
        new(code, "DB::Exception", $"Code: {code}", string.Empty);

    [Theory]
    [InlineData(ClickHouseFault.MemoryLimitExceeded)]
    [InlineData(ClickHouseFault.CannotAllocateMemory)]
    [InlineData(ClickHouseFault.TooManySimultaneousQueries)]
    [InlineData(ClickHouseFault.TimeoutExceeded)]
    [InlineData(ClickHouseFault.NetworkError)]
    [InlineData(ClickHouseFault.SocketTimeout)]
    [InlineData(ClickHouseFault.NoFreeConnection)]
    [InlineData(ClickHouseFault.TooManyParts)]
    public void ServerOverloadAndNetworkCodesAreTransient(int code)
    {
        ClickHouseFault.IsTransient(Server(code)).Should().BeTrue();
        ClickHouseFault.IsConnectionFault(Server(code)).Should().BeFalse();
    }

    [Theory]
    [InlineData(47)]
    [InlineData(62)]
    [InlineData(60)]
    public void QueryMistakesAreNotTransient(int code)
    {
        ClickHouseFault.IsTransient(Server(code)).Should().BeFalse();
    }

    [Fact]
    public void ADroppedConnectionIsATransientConnectionFault()
    {
        Exception[] dropped =
        [
            new ClickHouseException(ClickHouseErrorCodes.ConnectionClosed, "closed"),
            new ClickHouseException(ClickHouseErrorCodes.NetworkError, "reset", new IOException("Broken pipe")),
            new ClickHouseException(ClickHouseErrorCodes.InvalidConnectionState, "state"),
            new IOException("Reached an unexpected end of the server's response"),
            new InvalidOperationException("wrapped", new SocketException(104)),
        ];

        foreach (Exception exception in dropped)
        {
            ClickHouseFault.IsTransient(exception).Should().BeTrue(exception.Message);
            ClickHouseFault.IsConnectionFault(exception).Should().BeTrue(exception.Message);
        }
    }

    [Fact]
    public void AMemoryErrorWrappedByTheClientIsStillTransient()
    {
        Exception wrapped = new InvalidOperationException("read failed", Server(ClickHouseFault.MemoryLimitExceeded));

        ClickHouseFault.IsTransient(wrapped).Should().BeTrue();
    }

    [Fact]
    public void OtherFailuresAreNot()
    {
        ClickHouseFault.IsTransient(new InvalidOperationException("bug")).Should().BeFalse();
        ClickHouseFault.IsTransient(new ClickHouseException(ClickHouseErrorCodes.InvalidQueryParameterConfiguration, "bad")).Should().BeFalse();
        ClickHouseFault.IsConnectionFault(new TimeoutException()).Should().BeFalse();
    }

    private static ClickHouseConnectionFactory Factory() =>
        new(Options.Create(new TelemetryEngineOptions
        {
            ClickHouseConnectionString = "Host=127.0.0.1;Port=9000;Database=nealytics_core;User=default;Password=;",
            ConnectionPoolSize = 4,
        }));

    private static async Task<HttpContext> RunAsync(Exception? thrown)
    {
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/api/v1/analytics/pivot";
        StorageUnavailableMiddleware middleware = new(
            _ => thrown is null ? Task.CompletedTask : Task.FromException(thrown),
            NullLogger<StorageUnavailableMiddleware>.Instance);
        await middleware.InvokeAsync(context, Factory());
        return context;
    }

    [Fact]
    public async Task ATransientStorageFaultIsA503WithRetryAfter()
    {
        HttpContext context = await RunAsync(Server(ClickHouseFault.MemoryLimitExceeded));

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().Should().Be("5");
        context.Response.ContentType.Should().Be("application/problem+json");
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).Should().Contain("storage_unavailable");
    }

    [Fact]
    public async Task ADroppedConnectionIsA503()
    {
        HttpContext context = await RunAsync(new IOException("Broken pipe"));

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task AnyOtherFailureStillPropagates()
    {
        Func<Task> act = () => RunAsync(new InvalidOperationException("bug"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ASuccessfulRequestIsUntouched()
    {
        HttpContext context = await RunAsync(null);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();
    }

    [Fact]
    public async Task AFaultAfterTheResponseStartedIsNotRewritten()
    {
        DefaultHttpContext context = new();
        StartedResponse started = new();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(started);
        StorageUnavailableMiddleware middleware = new(
            _ => Task.FromException(new IOException("Broken pipe")),
            NullLogger<StorageUnavailableMiddleware>.Instance);

        Func<Task> act = () => middleware.InvokeAsync(context, Factory());

        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task EvictingIdleConnectionsOfAnEmptyPoolEvictsNothing()
    {
        await using ClickHouseConnectionFactory factory = Factory();

        factory.EvictIdle().Should().Be(0);
    }

    private sealed class StartedResponse : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}

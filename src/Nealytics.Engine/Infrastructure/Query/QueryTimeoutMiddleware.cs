namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nealytics.Engine.Infrastructure.Storage;

public sealed class QueryTimeoutMiddleware
{
    private readonly RequestDelegate _next;
    private readonly QueryGuard _guard;

    public QueryTimeoutMiddleware(RequestDelegate next, QueryGuard guard)
    {
        _next = next;
        _guard = guard;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted && ClickHouseTimeoutFault.IsTimeout(exception))
        {
            context.Response.Clear();
            await _guard.RejectTimeout().ExecuteAsync(context);
        }
    }
}

namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using Octonica.ClickHouseClient.Exceptions;

public static class ClickHouseTimeoutFault
{
    public const int TimeoutExceeded = 159;
    public const int TooSlow = 160;

    public static bool IsTimeout(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ClickHouseServerException server
                && server.ServerErrorCode is TimeoutExceeded or TooSlow)
            {
                return true;
            }
        }

        return false;
    }
}

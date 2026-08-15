namespace Nealytics.Engine.Infrastructure.Storage;

using System;

public static class ClickHouseArgumentFault
{
    public static bool IsUnknownTimeZone(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not Octonica.ClickHouseClient.Exceptions.ClickHouseServerException)
            {
                continue;
            }

            if (current.Message.Contains("time zone", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

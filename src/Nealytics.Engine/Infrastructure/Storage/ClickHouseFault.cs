namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Collections.Frozen;
using System.IO;
using System.Net.Sockets;
using Octonica.ClickHouseClient.Exceptions;

public static class ClickHouseFault
{
    public const int TimeoutExceeded = 159;
    public const int CannotAllocateMemory = 173;
    public const int TooManySimultaneousQueries = 202;
    public const int NoFreeConnection = 203;
    public const int SocketTimeout = 209;
    public const int NetworkError = 210;
    public const int MemoryLimitExceeded = 241;
    public const int TooManyParts = 252;

    private static readonly FrozenSet<int> TransientServerCodes = new[]
    {
        TimeoutExceeded,
        CannotAllocateMemory,
        TooManySimultaneousQueries,
        NoFreeConnection,
        SocketTimeout,
        NetworkError,
        MemoryLimitExceeded,
        TooManyParts,
    }.ToFrozenSet();

    private static readonly FrozenSet<int> TransientClientCodes = new[]
    {
        ClickHouseErrorCodes.ConnectionClosed,
        ClickHouseErrorCodes.NetworkError,
        ClickHouseErrorCodes.InvalidConnectionState,
    }.ToFrozenSet();

    public static bool IsConnectionFault(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ClickHouseServerException:
                    return false;
                case ClickHouseException client when TransientClientCodes.Contains(client.ErrorCode):
                    return true;
                case IOException:
                case SocketException:
                    return true;
            }
        }

        return false;
    }

    public static bool IsTransient(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ClickHouseServerException server:
                    return TransientServerCodes.Contains(server.ServerErrorCode);
                case ClickHouseException client when TransientClientCodes.Contains(client.ErrorCode):
                    return true;
                case IOException:
                case SocketException:
                case TimeoutException:
                    return true;
            }
        }

        return false;
    }
}

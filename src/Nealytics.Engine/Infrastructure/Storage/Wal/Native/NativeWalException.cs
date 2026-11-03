namespace Nealytics.Engine.Infrastructure.Storage.Wal.Native;

using System.ComponentModel;
using System.IO;

internal sealed class NativeWalException : IOException
{
    public NativeWalException(int errorCode, string operation)
        : base("Native WAL " + operation + " failed with errno " + errorCode + ".",
            new Win32Exception(errorCode))
    {
        ErrorCode = errorCode;
        Operation = operation;
    }

    public int ErrorCode { get; }
    public string Operation { get; }
}

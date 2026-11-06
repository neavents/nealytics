namespace Nealytics.Engine.Features.HttpImport;

using System;

internal sealed class HttpImportException(int statusCode, string error) : Exception(error)
{
    public int StatusCode { get; } = statusCode;
    public string Error { get; } = error;
}

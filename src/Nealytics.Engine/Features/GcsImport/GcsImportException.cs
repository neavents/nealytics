namespace Nealytics.Engine.Features.GcsImport;

using System;

internal sealed class GcsImportException(int statusCode, string error) : Exception(error)
{
    public int StatusCode { get; } = statusCode;
    public string Error { get; } = error;
}

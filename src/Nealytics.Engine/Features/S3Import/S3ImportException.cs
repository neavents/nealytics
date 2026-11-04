namespace Nealytics.Engine.Features.S3Import;

using System;

internal sealed class S3ImportException(int statusCode, string error) : Exception(error)
{
    public int StatusCode { get; } = statusCode;
    public string Error { get; } = error;
}

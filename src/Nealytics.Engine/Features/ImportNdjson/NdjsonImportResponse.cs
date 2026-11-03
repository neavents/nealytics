namespace Nealytics.Engine.Features.ImportNdjson;

using Microsoft.AspNetCore.Http;
using System.Text.Json.Serialization;

public sealed record NdjsonImportResponse(
    int AcceptedRecords, long NextByteOffset,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error)
{
    public int NextLine => AcceptedRecords + 1;
    public bool Complete => Error is null;

    internal int StatusCode => Error is null ? StatusCodes.Status202Accepted
        : Error is "line_too_large" or "body_too_large" ? StatusCodes.Status413PayloadTooLarge
        : StatusCodes.Status400BadRequest;
}

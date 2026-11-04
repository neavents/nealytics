namespace Nealytics.Engine.Features.ImportCsv;

using Microsoft.AspNetCore.Http;
using System.Text.Json.Serialization;

public sealed record CsvImportResponse(
    int AcceptedRecords, long NextByteOffset,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error)
{
    public int NextRecord => AcceptedRecords + 1;
    public bool Complete => Error is null;

    internal int StatusCode => Error is null ? StatusCodes.Status202Accepted
        : Error is "record_too_large" or "body_too_large" ? StatusCodes.Status413PayloadTooLarge
        : StatusCodes.Status400BadRequest;
}

namespace Nealytics.Engine.Features.SourceMapping;

using System;
using System.IO;
using System.Text.Json;

internal sealed record SourceField(string Target, string[] Path, string? Default, bool Scalar)
{
    public string? Read(JsonElement root)
    {
        JsonElement value = root;
        foreach (string segment in Path)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return Default;
            }
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Source mapping path crosses a non-object value.");
            }
            if (!value.TryGetProperty(segment, out value))
            {
                return Default;
            }
        }
        try
        {
            return Path.Length == 0 ? Default : value.ValueKind switch
            {
                JsonValueKind.Null => Default,
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False when Scalar => value.GetRawText(),
                _ => throw new InvalidDataException("Source mapping requires a compatible scalar value.")
            };
        }
        catch (InvalidOperationException)
        {
            throw new InvalidDataException("Source mapping contains invalid Unicode text.");
        }
    }
}

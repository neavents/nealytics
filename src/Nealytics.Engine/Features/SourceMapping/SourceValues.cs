namespace Nealytics.Engine.Features.SourceMapping;

using System.Collections.Generic;
using System.Text.Json;

internal interface ISourceValues
{
    string? Read(SourceField field);
}

internal readonly struct JsonSourceValues(JsonElement root) : ISourceValues
{
    public string? Read(SourceField field) => field.Read(root);
}

internal readonly struct ColumnSourceValues(
    IReadOnlyDictionary<string, int> columns, IReadOnlyList<string> cells) : ISourceValues
{
    public string? Read(SourceField field) => field.Path.Length == 0
        ? field.Default : cells[columns[field.Path[0]]];
}

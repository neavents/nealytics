namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Nealytics.Engine.Infrastructure.Configuration;

internal static class ComputedDimensionSql
{
    internal static string Build(ComputedDimension definition, string source)
    {
        string text = "toString(" + source + ")";
        return definition.Operation switch
        {
            "lower" => "lowerUTF8(" + text + ")",
            "upper" => "upperUTF8(" + text + ")",
            "trim" => "trimBoth(" + text + ")",
            "substring" => "substringUTF8(" + text + ", " + definition.Start.ToString(CultureInfo.InvariantCulture)
                + ", " + definition.Length.ToString(CultureInfo.InvariantCulture) + ")",
            "map" => Map(definition, source, text),
            "bucket" => Bucket(definition.Width!.Value, source),
            _ => throw new InvalidOperationException("Unknown compiled dimension operation.")
        };
    }

    private static string Map(ComputedDimension definition, string source, string text)
    {
        string keys = string.Join(", ", definition.Mapping.Select(mapping => Literal(mapping.From)));
        string values = string.Join(", ", definition.Mapping.Select(mapping => Literal(mapping.To)));
        string fallback = definition.Default is string value ? Literal(value) : text;
        return "if(isNull(" + source + "), NULL, transform(assumeNotNull(" + text + "), ["
            + keys + "], [" + values + "], " + fallback + "))";
    }

    private static string Bucket(double width, string source)
    {
        string step = "toFloat64('" + width.ToString(CultureInfo.InvariantCulture) + "')";
        string bucket = "floor(toFloat64(" + source + ") / " + step + ") * " + step;
        return "if(isFinite(" + bucket + "), " + bucket + ", NULL)";
    }

    private static string Literal(string value) =>
        "unhex('" + Convert.ToHexString(Encoding.UTF8.GetBytes(value)) + "')";
}

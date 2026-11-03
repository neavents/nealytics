namespace Nealytics.Engine.Infrastructure.Configuration;

using System;
using System.Globalization;
using System.Numerics;

internal static class MeasureValueRules
{
    internal static bool TryParse<T>(Measure measure, string raw, out T parsed)
        where T : struct, INumber<T> =>
        T.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
        && (measure.Minimum is not double minimum || double.CreateSaturating(parsed) >= minimum)
        && (measure.Maximum is not double maximum || double.CreateSaturating(parsed) <= maximum);

    internal static bool Accepts(Measure measure, string raw) => measure.Kind switch
    {
        MeasureValueKind.UInt8 => TryParse(measure, raw, out byte _),
        MeasureValueKind.UInt16 => TryParse(measure, raw, out ushort _),
        MeasureValueKind.UInt32 => TryParse(measure, raw, out uint _),
        MeasureValueKind.UInt64 => TryParse(measure, raw, out ulong _),
        MeasureValueKind.Int16 => TryParse(measure, raw, out short _),
        MeasureValueKind.Int32 => TryParse(measure, raw, out int _),
        MeasureValueKind.Int64 => TryParse(measure, raw, out long _),
        MeasureValueKind.Float32 => TryParse(measure, raw, out float _),
        MeasureValueKind.Float64 => TryParse(measure, raw, out double _),
        MeasureValueKind.Decimal => TryParse(measure, raw, out decimal _),
        _ => throw new ArgumentOutOfRangeException(nameof(measure), measure.Kind, "Unhandled measure value kind.")
    };
}

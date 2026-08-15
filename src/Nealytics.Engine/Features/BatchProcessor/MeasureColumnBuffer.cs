namespace Nealytics.Engine.Features.BatchProcessor;

using System;
using System.Buffers;
using System.Globalization;
using System.Numerics;
using Nealytics.Engine.Infrastructure.Configuration;

internal abstract class MeasureColumnBuffer : IDisposable
{
    protected MeasureColumnBuffer(Measure measure)
    {
        Measure = measure;
    }

    internal Measure Measure { get; }

    internal int RejectedValueCount { get; private set; }

    internal void Set(int index, string? raw)
    {
        if (!TrySet(index, raw))
        {
            RejectedValueCount++;
            SetAbsent(index);
        }
    }

    internal abstract object BuildSegment(int count);

    public abstract void Dispose();

    protected abstract bool TrySet(int index, string? raw);

    protected abstract void SetAbsent(int index);

    protected bool WithinDeclaredBounds(double value) =>
        (Measure.Minimum is not double minimum || value >= minimum)
        && (Measure.Maximum is not double maximum || value <= maximum);

    internal static MeasureColumnBuffer Create(Measure measure, int capacity) => measure.Kind switch
    {
        MeasureValueKind.UInt8 => new NumericBuffer<byte>(measure, capacity),
        MeasureValueKind.UInt16 => new NumericBuffer<ushort>(measure, capacity),
        MeasureValueKind.UInt32 => new NumericBuffer<uint>(measure, capacity),
        MeasureValueKind.UInt64 => new NumericBuffer<ulong>(measure, capacity),
        MeasureValueKind.Int16 => new NumericBuffer<short>(measure, capacity),
        MeasureValueKind.Int32 => new NumericBuffer<int>(measure, capacity),
        MeasureValueKind.Int64 => new NumericBuffer<long>(measure, capacity),
        MeasureValueKind.Float32 => new NumericBuffer<float>(measure, capacity),
        MeasureValueKind.Float64 => new NumericBuffer<double>(measure, capacity),
        MeasureValueKind.Decimal => new NumericBuffer<decimal>(measure, capacity),
        _ => throw new ArgumentOutOfRangeException(
            nameof(measure), measure.Kind, "Unhandled measure value kind."),
    };

    private sealed class NumericBuffer<T> : MeasureColumnBuffer
        where T : struct, INumber<T>
    {
        private readonly T?[] _values;

        internal NumericBuffer(Measure measure, int capacity) : base(measure)
        {
            _values = ArrayPool<T?>.Shared.Rent(capacity);
        }

        protected override bool TrySet(int index, string? raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                _values[index] = null;
                return true;
            }

            if (!T.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out T parsed))
            {
                return false;
            }

            if (!WithinDeclaredBounds(double.CreateSaturating(parsed)))
            {
                return false;
            }

            _values[index] = parsed;
            return true;
        }

        protected override void SetAbsent(int index) => _values[index] = null;

        internal override object BuildSegment(int count) => new ArraySegment<T?>(_values, 0, count);

        public override void Dispose() => ArrayPool<T?>.Shared.Return(_values);
    }
}

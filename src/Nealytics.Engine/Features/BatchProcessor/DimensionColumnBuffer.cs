namespace Nealytics.Engine.Features.BatchProcessor;

using System;
using System.Buffers;
using System.Globalization;
using Nealytics.Engine.Infrastructure.Configuration;

internal abstract class DimensionColumnBuffer : IDisposable
{
    protected DimensionColumnBuffer(Dimension dimension)
    {
        Dimension = dimension;
    }

    internal Dimension Dimension { get; }

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

    internal static DimensionColumnBuffer Create(Dimension dimension, int capacity) => dimension.Kind switch
    {
        DimensionValueKind.String => new StringBuffer(dimension, capacity),
        DimensionValueKind.LowCardinalityString => new LowCardinalityBuffer(dimension, capacity),
        DimensionValueKind.UInt64 => new UInt64Buffer(dimension, capacity),
        DimensionValueKind.Int64 => new Int64Buffer(dimension, capacity),
        DimensionValueKind.DateTime => new DateTimeBuffer(dimension, capacity),
        _ => throw new ArgumentOutOfRangeException(
            nameof(dimension), dimension.Kind, "Unhandled dimension value kind."),
    };

    private sealed class StringBuffer : DimensionColumnBuffer
    {
        private readonly string?[] _values;

        internal StringBuffer(Dimension dimension, int capacity) : base(dimension)
        {
            _values = ArrayPool<string?>.Shared.Rent(capacity);
        }

        protected override bool TrySet(int index, string? raw)
        {
            _values[index] = string.IsNullOrEmpty(raw) ? null : raw;
            return true;
        }

        protected override void SetAbsent(int index) => _values[index] = null;

        internal override object BuildSegment(int count) => new ArraySegment<string?>(_values, 0, count);

        public override void Dispose() => ArrayPool<string?>.Shared.Return(_values, true);
    }

    private sealed class LowCardinalityBuffer : DimensionColumnBuffer
    {
        private readonly string[] _values;

        internal LowCardinalityBuffer(Dimension dimension, int capacity) : base(dimension)
        {
            _values = ArrayPool<string>.Shared.Rent(capacity);
        }

        protected override bool TrySet(int index, string? raw)
        {
            _values[index] = raw ?? string.Empty;
            return true;
        }

        protected override void SetAbsent(int index) => _values[index] = string.Empty;

        internal override object BuildSegment(int count) => new ArraySegment<string>(_values, 0, count);

        public override void Dispose() => ArrayPool<string>.Shared.Return(_values, true);
    }

    private sealed class UInt64Buffer : DimensionColumnBuffer
    {
        private readonly ulong?[] _values;

        internal UInt64Buffer(Dimension dimension, int capacity) : base(dimension)
        {
            _values = ArrayPool<ulong?>.Shared.Rent(capacity);
        }

        protected override bool TrySet(int index, string? raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                _values[index] = null;
                return true;
            }

            if (ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed))
            {
                _values[index] = parsed;
                return true;
            }

            return false;
        }

        protected override void SetAbsent(int index) => _values[index] = null;

        internal override object BuildSegment(int count) => new ArraySegment<ulong?>(_values, 0, count);

        public override void Dispose() => ArrayPool<ulong?>.Shared.Return(_values);
    }

    private sealed class Int64Buffer : DimensionColumnBuffer
    {
        private readonly long?[] _values;

        internal Int64Buffer(Dimension dimension, int capacity) : base(dimension)
        {
            _values = ArrayPool<long?>.Shared.Rent(capacity);
        }

        protected override bool TrySet(int index, string? raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                _values[index] = null;
                return true;
            }

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                _values[index] = parsed;
                return true;
            }

            return false;
        }

        protected override void SetAbsent(int index) => _values[index] = null;

        internal override object BuildSegment(int count) => new ArraySegment<long?>(_values, 0, count);

        public override void Dispose() => ArrayPool<long?>.Shared.Return(_values);
    }

    private sealed class DateTimeBuffer : DimensionColumnBuffer
    {
        private readonly DateTimeOffset?[] _values;

        internal DateTimeBuffer(Dimension dimension, int capacity) : base(dimension)
        {
            _values = ArrayPool<DateTimeOffset?>.Shared.Rent(capacity);
        }

        protected override bool TrySet(int index, string? raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                _values[index] = null;
                return true;
            }

            if (DateTime.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTime parsed))
            {
                _values[index] = TelemetryInsertMath.ToClickHouseTimestamp(parsed);
                return true;
            }

            return false;
        }

        protected override void SetAbsent(int index) => _values[index] = null;

        internal override object BuildSegment(int count) => new ArraySegment<DateTimeOffset?>(_values, 0, count);

        public override void Dispose() => ArrayPool<DateTimeOffset?>.Shared.Return(_values);
    }
}

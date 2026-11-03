namespace Nealytics.Engine.Infrastructure.Query;

using System;
using System.Globalization;
using System.Numerics;

internal readonly record struct FormulaNumber(BigInteger Numerator, BigInteger Denominator)
{
    internal static FormulaNumber? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512)
        {
            return null;
        }
        ReadOnlySpan<char> text = value.AsSpan();
        int exponentIndex = text.IndexOfAny('e', 'E');
        int exponent = 0;
        if (exponentIndex != -1)
        {
            if (!int.TryParse(text[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out exponent) || exponent is < -308 or > 308)
            {
                return null;
            }
            text = text[..exponentIndex];
        }
        bool negative = false;
        if (!text.IsEmpty && text[0] is '+' or '-')
        {
            negative = text[0] == '-';
            text = text[1..];
        }
        BigInteger numerator = BigInteger.Zero;
        bool hasDigits = false;
        int scale = -1;
        foreach (char character in text)
        {
            if (character == '.' && scale < 0)
            {
                scale = 0;
                continue;
            }
            if (character is < '0' or > '9')
            {
                return null;
            }
            numerator = numerator * 10 + character - '0';
            hasDigits = true;
            if (scale >= 0)
            {
                scale++;
            }
        }
        if (!hasDigits)
        {
            return null;
        }
        int power = exponent - Math.Max(scale, 0);
        numerator *= BigInteger.Pow(10, Math.Max(power, 0));
        BigInteger denominator = BigInteger.Pow(10, Math.Max(-power, 0));
        return Create(negative ? -numerator : numerator, denominator);
    }

    internal static FormulaNumber? Apply(string operation, FormulaNumber left, FormulaNumber right) =>
        operation switch
        {
            "add" => Create(left.Numerator * right.Denominator + right.Numerator * left.Denominator,
                left.Denominator * right.Denominator),
            "subtract" => Create(left.Numerator * right.Denominator - right.Numerator * left.Denominator,
                left.Denominator * right.Denominator),
            "multiply" => Create(left.Numerator * right.Numerator, left.Denominator * right.Denominator),
            "divide" => Create(left.Numerator * right.Denominator, left.Denominator * right.Numerator),
            _ => throw new InvalidOperationException("Unknown compiled formula operation.")
        };

    internal FormulaNumber Negate() => new(-Numerator, Denominator);

    internal string Format()
    {
        BigInteger magnitude = BigInteger.Abs(Numerator);
        BigInteger scale = BigInteger.Pow(10, 34);
        BigInteger rounded = BigInteger.DivRem(magnitude * scale, Denominator, out BigInteger remainder);
        if (remainder * 2 >= Denominator)
        {
            rounded++;
        }
        BigInteger integer = BigInteger.DivRem(rounded, scale, out BigInteger fraction);
        string sign = Numerator.Sign == -1 && !rounded.IsZero ? "-" : string.Empty;
        string whole = integer.ToString(CultureInfo.InvariantCulture);
        if (fraction.IsZero)
        {
            return sign + whole;
        }
        string tail = fraction.ToString("D34", CultureInfo.InvariantCulture).TrimEnd('0');
        return sign + whole + "." + tail;
    }

    private static FormulaNumber? Create(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            return null;
        }
        if (denominator.Sign == -1)
        {
            numerator = -numerator;
            denominator = -denominator;
        }
        BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        numerator /= divisor;
        denominator /= divisor;
        return BigInteger.Abs(numerator).GetBitLength() > 4096 || denominator.GetBitLength() > 4096
            ? null : new FormulaNumber(numerator, denominator);
    }
}

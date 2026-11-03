// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Globalization;
using System.Numerics;
using System.Text;
using PbFloat = Protowire.Pb.BigFloat;

namespace Protowire.Pxf;

/// <summary>
/// The text form of a <c>pxf.BigFloat</c> (draft -01 § PXF Annotations): the
/// unsigned integer <c>mantissa</c> × 2^<c>exponent</c>, at <c>prec</c> bits,
/// written as a decimal literal with a precision suffix. Reading rounds to
/// nearest, ties to even, and writing is exact, so a value reads back as itself.
/// </summary>
internal static class BigFloatText
{
    private const int DefaultPrec = 256;

    // floor(log10(2) × 2^128).
    private static readonly BigInteger Log10Of2 =
        (new BigInteger(0x4d104d427de7fbccUL) << 64) | new BigInteger(0x47c4acd605be48bcUL);

    /// <summary>
    /// The significant digits a BigFloat of <paramref name="prec"/> bits is
    /// written with: the decimal digits of 2^prec, plus one, the fewest that
    /// recover every value. 2^prec is never computed, because prec comes off
    /// the wire or out of a document.
    /// </summary>
    public static int Digits(uint prec) => (int)((prec * Log10Of2) >> 128) + 2;

    /// <summary>Reads <paramref name="literal"/>, with its suffix's digits in <paramref name="suffix"/>.</summary>
    /// <exception cref="FormatException">The literal is not a value this type can hold.</exception>
    public static PbFloat Parse(string literal, string suffix, int maxDigits, int maxExponent)
    {
        uint prec = DefaultPrec;
        bool suffixed = suffix.Length > 0;
        if (suffixed)
        {
            if (suffix.Length > 10 || !uint.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out prec) || prec == 0)
            {
                throw new FormatException($"big float literal {Clip(literal)}p{Clip(suffix)}: precision suffix p{Clip(suffix)} is not a positive bit count");
            }
            int digits = Digits(prec);
            if (digits > maxDigits)
            {
                throw new FormatException($"big float literal {Clip(literal)}p{prec}: precision p{prec} renders to {digits} digits; MaxNumericLiteralDigits={maxDigits}");
            }
        }
        int count = literal.Length <= maxDigits ? 0 : Decoder.CountDigits(literal);
        if (count > maxDigits)
        {
            throw new FormatException($"numeric literal has {count} digits; MaxNumericLiteralDigits={maxDigits}");
        }

        var (negative, digitsOnly, exp10) = Split(literal);
        var coefficient = BigInteger.Parse(digitsOnly, NumberStyles.None, CultureInfo.InvariantCulture);
        if (coefficient.IsZero)
        {
            return new PbFloat(BigInteger.Zero, 0, suffixed ? prec : 0, negative);
        }

        // big.Float holds the value's binary exponent before applying the
        // power of five, and refuses one outside an int32 as a scan error.
        Int128 exp2 = (Int128)(long)coefficient.GetBitLength() + exp10;
        if (exp2 < int.MinValue || exp2 > int.MaxValue)
        {
            throw new FormatException($"invalid big float: {literal}");
        }

        // A thirteen-byte literal can name a power of ten no one should
        // build, and every value this far out is refused. A decimal digit is
        // over three bits. Out here, protowire-go's refusal is found as it
        // finds it, at its working precision.
        long adjusted = exp10 + digitsOnly.TrimStart('0').Length;
        if (Math.Abs(adjusted) > maxExponent / 3 + 2)
        {
            RefuseAsGo(literal, coefficient, exp10, prec, maxExponent);
        }

        BigInteger num = coefficient, den = BigInteger.One;
        if (exp10 >= 0) num *= BigInteger.Pow(10, (int)exp10);
        else den = BigInteger.Pow(10, (int)-exp10);
        var (mantissa, exponent) = Round(num, den, prec);
        long binary = exponent + prec;
        if (Math.Abs(binary) > maxExponent)
        {
            throw new FormatException($"big float literal {Clip(literal)} has binary exponent {binary}, and MaxBigFloatExponent={maxExponent} bounds its magnitude");
        }
        return new PbFloat(mantissa, (int)exponent, prec, negative);
    }

    /// <summary>Reads a literal that arrives as one string, as a default does.</summary>
    public static PbFloat Parse(string literal, int maxDigits, int maxExponent)
    {
        int p = literal.IndexOf('p');
        if (p < 0) return Parse(literal, "", maxDigits, maxExponent);
        if (p == literal.Length - 1)
        {
            throw new FormatException($"big float literal {Clip(literal)}: precision suffix p is not a positive bit count");
        }
        return Parse(literal[..p], literal[(p + 1)..], maxDigits, maxExponent);
    }

    /// <summary>Writes a value as its literal, suffix included.</summary>
    /// <exception cref="FormatException">The value has no literal within the limits.</exception>
    public static string Format(PbFloat v, int maxDigits, int maxExponent)
    {
        if (v.Precision == 0) return v.Negative ? "-0" : "0";
        if (!v.Mantissa.IsZero)
        {
            long binary = v.Exponent + (long)v.Mantissa.GetBitLength();
            if (Math.Abs(binary) > maxExponent)
            {
                throw new FormatException($"pxf.BigFloat binary exponent {binary} exceeds MaxBigFloatExponent={maxExponent}");
            }
        }
        int digits = Digits(v.Precision);
        if (digits > maxDigits)
        {
            throw new FormatException($"pxf.BigFloat prec {v.Precision} bits renders to {digits} digits; MaxNumericLiteralDigits={maxDigits}");
        }
        string suffix = "p" + v.Precision.ToString(CultureInfo.InvariantCulture);
        string sign = v.Negative ? "-" : "";
        if (v.Mantissa.IsZero) return sign + "0" + suffix;

        // A mantissa wider than prec is the value rounded to prec.
        var (mantissa, exponent) = v.Mantissa.GetBitLength() > v.Precision
            ? Round(v.Mantissa, BigInteger.One, v.Precision, v.Exponent)
            : (v.Mantissa, (long)v.Exponent);

        // Exact decimal digits: m × 2^e, or m × 5^-e with the point moved.
        BigInteger exact = exponent >= 0 ? mantissa << (int)exponent : mantissa * BigInteger.Pow(5, (int)-exponent);
        string all = exact.ToString(CultureInfo.InvariantCulture);
        int point = exponent >= 0 ? all.Length : all.Length + (int)exponent;
        return sign + General(all, point, digits) + suffix;
    }

    // What protowire-go says of a literal far past MaxBigFloatExponent. It
    // parses with math/big, which multiplies the coefficient by a power of
    // five it builds at prec + 64 bits (Float.scan, Float.pow5). That cannot
    // overflow its memory, only its exponent range, so the value becomes
    // infinite or zero there, and otherwise has a binary exponent the
    // message names. The checks run in parseBigFloatPrec's order. Returns
    // only for a value no check refuses, which the caller then reads exactly.
    private static void RefuseAsGo(string literal, BigInteger coefficient, long exp10, uint prec, int maxExponent)
    {
        var z = new GoFloat(coefficient, exp10, false);
        GoFloat v = exp10 == 0
            ? GoFloat.Rounded(coefficient, BigInteger.One, 0, prec)
            : exp10 > 0
                ? GoFloat.Mul(z, GoFloat.Pow5((ulong)exp10, prec + 64), prec)
                : GoFloat.Quo(z, GoFloat.Pow5((ulong)-(Int128)exp10, prec + 64), prec);
        if (v.Inf)
        {
            throw new FormatException($"big float literal {Clip(literal)} is above big.Float's range");
        }
        if (v.Mant.IsZero)
        {
            throw new FormatException($"big float literal {Clip(literal)} is below big.Float's range");
        }
        if (prec > int.MaxValue)
        {
            throw new FormatException($"big float literal {Clip(literal)}: precision p{prec} exceeds the widest mantissa big.Float can hold, {int.MaxValue} bits");
        }
        long e = v.MantExp;
        if (e - prec < int.MinValue)
        {
            throw new FormatException($"big float literal {Clip(literal)} is below the wire's exponent range");
        }
        if (Math.Abs(e) > maxExponent)
        {
            throw new FormatException($"big float literal {Clip(literal)} has binary exponent {e}, and MaxBigFloatExponent={maxExponent} bounds its magnitude");
        }
    }

    /// <summary>
    /// A math/big Float: Mant × 2^Exp, or infinite, or zero for a zero
    /// Mant. Each operation rounds to nearest, ties to even, at the precision
    /// given, and leaves big.Float's exponent range as Go does: an exact
    /// exponent past it is infinite or zero, and a carry past its top is
    /// infinite (Float.setExpAndRound, Float.round).
    /// </summary>
    private readonly record struct GoFloat(BigInteger Mant, long Exp, bool Inf)
    {
        private static readonly GoFloat Infinite = new(BigInteger.Zero, 0, true);
        private static readonly GoFloat Zero = new(BigInteger.Zero, 0, false);

        /// <summary>The exponent of the value as 0.5 ≤ m &lt; 1 times a power of two, Float.MantExp's.</summary>
        public long MantExp => Exp + (long)Mant.GetBitLength();

        public static GoFloat Rounded(BigInteger num, BigInteger den, long shift, uint prec)
        {
            long d = (long)num.GetBitLength() - (long)den.GetBitLength();
            bool atLeast = d >= 0 ? num >= den << (int)d : num << (int)-d >= den;
            long exact = d + (atLeast ? 1 : 0) + shift;
            if (exact > int.MaxValue) return Infinite;
            if (exact < int.MinValue) return Zero;
            var (m, e) = Round(num, den, prec, shift);
            var v = new GoFloat(m, e, false);
            return v.MantExp > int.MaxValue ? Infinite : v;
        }

        public static GoFloat Mul(GoFloat x, GoFloat y, uint prec) =>
            x.Inf || y.Inf ? Infinite : Rounded(x.Mant * y.Mant, BigInteger.One, x.Exp + y.Exp, prec);

        public static GoFloat Quo(GoFloat x, GoFloat y, uint prec) =>
            y.Inf ? Zero : x.Inf ? Infinite : Rounded(x.Mant, y.Mant, x.Exp - y.Exp, prec);

        // Float.pow5: 5^27 is the largest power that fits a uint64, and the
        // squares are kept at 64 bits more than the result.
        public static GoFloat Pow5(ulong n, uint prec)
        {
            if (n <= 27) return new GoFloat(BigInteger.Pow(5, (int)n), 0, false);
            var z = new GoFloat(BigInteger.Pow(5, 27), 0, false);
            n -= 27;
            var f = new GoFloat(5, 0, false);
            while (n > 0)
            {
                if ((n & 1) != 0) z = Mul(z, f, prec);
                f = Mul(f, f, prec + 64);
                n >>= 1;
            }
            return z;
        }
    }

    private static (bool Negative, string Digits, long Exp10) Split(string literal)
    {
        string s = literal;
        bool negative = s.StartsWith('-');
        if (negative) s = s[1..];
        long exp10 = 0;
        int e = s.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            string text = s[(e + 1)..];
            // An exponent too long for an int64 is an error, as math/big's
            // scan makes it one, even on a zero.
            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exp10))
            {
                throw new FormatException($"invalid big float: {literal}");
            }
            s = s[..e];
        }
        int dot = s.IndexOf('.');
        if (dot >= 0)
        {
            // Saturates: an exponent this far down is refused anyway, unless
            // the coefficient is zero, which ignores it.
            exp10 = (long)Int128.Max((Int128)exp10 - (s.Length - dot - 1), long.MinValue);
            s = s.Remove(dot, 1);
        }
        if (s.Length == 0 || !s.All(char.IsAsciiDigit)) throw new FormatException($"invalid big float: {literal}");
        return (negative, s, exp10);
    }

    // num/den × 2^shift as a prec-bit integer and its exponent, rounding to
    // nearest with ties to even. The quotient keeps two bits past prec, and
    // what division leaves over is the sticky bit.
    private static (BigInteger Mantissa, long Exponent) Round(BigInteger num, BigInteger den, uint prec, long shift = 0)
    {
        long extra = prec + 2 - ((long)num.GetBitLength() - (long)den.GetBitLength());
        var q = BigInteger.DivRem(extra >= 0 ? num << (int)extra : num, extra >= 0 ? den : den << (int)-extra, out var r);
        bool sticky = !r.IsZero;
        long exponent = shift - extra;

        int drop = (int)((long)q.GetBitLength() - prec);
        var low = q & ((BigInteger.One << drop) - 1);
        var mantissa = q >> drop;
        exponent += drop;
        var half = BigInteger.One << (drop - 1);
        if (low > half || (low == half && (sticky || !mantissa.IsEven)))
        {
            mantissa += 1;
            if ((long)mantissa.GetBitLength() > prec)
            {
                mantissa >>= 1;
                exponent += 1;
            }
        }
        return (mantissa, exponent);
    }

    // C's %g: `digits` holds the exact significant digits and the point sits
    // `point` places into them. Rounds half to even, drops trailing zeros, and
    // takes the exponent form when the exponent is below -4 or at the precision.
    private static string General(string digits, int point, int precision)
    {
        string d = digits.TrimEnd('0');
        if (d.Length > precision)
        {
            bool up = d[precision] == '5' && precision + 1 == d.Length
                ? (d[precision - 1] - '0') % 2 != 0
                : d[precision] >= '5';
            d = d[..precision];
            if (up)
            {
                var chars = d.ToCharArray();
                int i = chars.Length - 1;
                while (i >= 0 && chars[i] == '9') i--;
                if (i < 0)
                {
                    d = "1";
                    point++;
                }
                else
                {
                    chars[i]++;
                    d = new string(chars, 0, i + 1);
                }
            }
            d = d.TrimEnd('0');
        }

        int eprec = precision;
        if (eprec > d.Length && d.Length >= point) eprec = d.Length;
        int exp = point - 1;
        var sb = new StringBuilder();
        if (exp < -4 || exp >= eprec)
        {
            sb.Append(d[0]);
            if (d.Length > 1) sb.Append('.').Append(d, 1, d.Length - 1);
            sb.Append('e').Append(exp < 0 ? '-' : '+');
            sb.Append(Math.Abs(exp).ToString("D2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
        if (point <= 0)
        {
            return sb.Append("0.").Append('0', -point).Append(d).ToString();
        }
        if (d.Length <= point)
        {
            return sb.Append(d).Append('0', point - d.Length).ToString();
        }
        return sb.Append(d, 0, point).Append('.').Append(d, point, d.Length - point).ToString();
    }

    private static string Clip(string s) => s.Length <= 24 ? s : s[..24] + "…";
}

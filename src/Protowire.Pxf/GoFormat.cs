// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Globalization;
using System.Text;

namespace Protowire.Pxf;

/// <summary>
/// The spellings the reference gets from Go's standard library, which every
/// port's text is compared against: <c>strconv.Quote</c>, the shortest
/// <c>%g</c> of a float, and <c>time.Duration.String</c>.
/// </summary>
internal static class GoFormat
{
    /// <summary>strconv.Quote, as the formatter and a non-identifier map key spell a string.</summary>
    public static void Quote(StringBuilder sb, string s)
    {
        const string Hex = "0123456789abcdef";
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); continue;
                case '\\': sb.Append("\\\\"); continue;
                case '\a': sb.Append("\\a"); continue;
                case '\b': sb.Append("\\b"); continue;
                case '\f': sb.Append("\\f"); continue;
                case '\n': sb.Append("\\n"); continue;
                case '\r': sb.Append("\\r"); continue;
                case '\t': sb.Append("\\t"); continue;
                case '\v': sb.Append("\\v"); continue;
            }
            if (c < 0x20 || c == 0x7F)
            {
                sb.Append("\\x").Append(Hex[c >> 4]).Append(Hex[c & 0xF]);
                continue;
            }
            int rune = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])
                ? char.ConvertToUtf32(c, s[i + 1])
                : c;
            int width = rune > 0xFFFF ? 2 : 1;
            if (c < 0x7F || IsPrint(s, i))
            {
                sb.Append(s, i, width);
            }
            else if (rune > 0xFFFF)
            {
                sb.Append("\\U").Append(rune.ToString("x8", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append("\\u").Append(rune.ToString("x4", CultureInfo.InvariantCulture));
            }
            i += width - 1;
        }
        sb.Append('"');
    }

    // Go's IsPrint: letters, marks, numbers, punctuation and symbols.
    private static bool IsPrint(string s, int i) => CharUnicodeInfo.GetUnicodeCategory(s, i) switch
    {
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator => false,
        _ => true,
    };

    /// <summary>strconv.AppendFloat with 'g' and precision -1, plus PXF's non-finite words.</summary>
    public static string Float(double value, bool single)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        string r = single
            ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
        return General(r);
    }

    // Shortest digits, in %e form when the exponent is below -4 or at least 6,
    // which is the threshold strconv uses for a shortest %g.
    private static string General(string r)
    {
        bool negative = r[0] == '-';
        if (negative) r = r[1..];
        int exp = 0;
        int e = r.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exp = int.Parse(r.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r[..e];
        }
        int dot = r.IndexOf('.');
        string digits = dot >= 0 ? r.Remove(dot, 1) : r;
        int point = (dot >= 0 ? dot : r.Length) + exp;
        string trimmed = digits.TrimStart('0');
        point -= digits.Length - trimmed.Length;
        digits = trimmed.TrimEnd('0');

        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        if (digits.Length == 0) return sb.Append('0').ToString();
        int x = point - 1;
        if (x < -4 || x >= 6) return Exponent(sb, digits, x);
        if (point <= 0) return sb.Append("0.").Append('0', -point).Append(digits).ToString();
        if (digits.Length <= point) return sb.Append(digits).Append('0', point - digits.Length).ToString();
        return sb.Append(digits, 0, point).Append('.').Append(digits, point, digits.Length - point).ToString();
    }

    private static string Exponent(StringBuilder sb, string digits, int x)
    {
        sb.Append(digits[0]);
        if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
        sb.Append('e').Append(x < 0 ? '-' : '+');
        return sb.Append(Math.Abs(x).ToString("D2", CultureInfo.InvariantCulture)).ToString();
    }

    /// <summary>time.Duration.String, for a duration of <paramref name="nanos"/>.</summary>
    public static string Duration(long nanos)
    {
        if (nanos == 0) return "0s";
        bool negative = nanos < 0;
        ulong u = negative ? (ulong)(-(nanos + 1)) + 1 : (ulong)nanos;
        var sb = new StringBuilder();
        if (u < 1_000_000_000)
        {
            if (u < 1_000) sb.Append(u).Append("ns");
            else if (u < 1_000_000) Fraction(sb, u, 1_000).Append("µs");
            else Fraction(sb, u, 1_000_000).Append("ms");
        }
        else
        {
            ulong seconds = u / 1_000_000_000;
            ulong hours = seconds / 3600;
            ulong minutes = seconds / 60 % 60;
            if (hours > 0) sb.Append(hours).Append('h');
            if (hours > 0 || minutes > 0) sb.Append(minutes).Append('m');
            Fraction(sb, u % 60_000_000_000, 1_000_000_000).Append('s');
        }
        if (negative) sb.Insert(0, '-');
        return sb.ToString();
    }

    private static StringBuilder Fraction(StringBuilder sb, ulong value, ulong unit)
    {
        sb.Append(value / unit);
        ulong rest = value % unit;
        if (rest == 0) return sb;
        int width = unit.ToString(CultureInfo.InvariantCulture).Length - 1;
        return sb.Append('.').Append(rest.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0').TrimEnd('0'));
    }
}

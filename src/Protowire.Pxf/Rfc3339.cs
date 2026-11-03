// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pxf;

/// <summary>
/// RFC 3339 timestamps at nanosecond precision. <see cref="DateTime"/> stops at
/// 100 ns and at year 1, so a <c>google.protobuf.Timestamp</c> does not fit it.
/// </summary>
internal static class Rfc3339
{
    private const long SecondsToEpoch = 62135596800L; // 0001-01-01 to 1970-01-01

    /// <summary>Parses <c>YYYY-MM-DDThh:mm:ss[.f…](Z|±hh:mm)</c>, as Go's RFC3339 layout does.</summary>
    public static bool TryParse(string s, out long seconds, out int nanos)
    {
        seconds = 0;
        nanos = 0;
        if (s.Length < 20) return false;
        if (!Digits(s, 0, 4, out int year) || s[4] != '-'
            || !Digits(s, 5, 2, out int month) || s[7] != '-'
            || !Digits(s, 8, 2, out int day) || s[10] != 'T'
            || !Digits(s, 11, 2, out int hour) || s[13] != ':'
            || !Digits(s, 14, 2, out int minute) || s[16] != ':'
            || !Digits(s, 17, 2, out int second))
        {
            return false;
        }
        int i = 19;
        if (s[i] == '.')
        {
            int start = ++i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            if (i == start) return false;
            long scale = 100_000_000;
            for (int k = start; k < i && scale > 0; k++, scale /= 10)
            {
                nanos += (int)((s[k] - '0') * scale);
            }
        }
        if (i >= s.Length) return false;
        int offset = 0;
        if (s[i] == 'Z')
        {
            i++;
        }
        else if (s[i] is '+' or '-')
        {
            if (i + 6 != s.Length || s[i + 3] != ':'
                || !Digits(s, i + 1, 2, out int oh) || !Digits(s, i + 4, 2, out int om)
                || oh > 23 || om > 59)
            {
                return false;
            }
            offset = (oh * 3600 + om * 60) * (s[i] == '-' ? -1 : 1);
            i += 6;
        }
        else
        {
            return false;
        }
        if (i != s.Length) return false;
        if (month is < 1 or > 12 || hour > 23 || minute > 59 || second > 59) return false;
        if (day < 1 || day > DaysIn(year, month)) return false;
        seconds = DaysFromYear1(year, month, day) * 86400L + hour * 3600L + minute * 60L + second
            - offset - SecondsToEpoch;
        return true;
    }

    /// <summary>
    /// Writes a Timestamp as Go's RFC3339Nano does: UTC, no trailing zeros in
    /// the fraction, and no fraction at all when it is zero.
    /// </summary>
    public static string Format(long seconds, int nanos)
    {
        long days = Math.DivRem(seconds + SecondsToEpoch, 86400L, out long rem);
        if (rem < 0) { rem += 86400; days--; }
        var (y, m, d) = CivilFromDays(days);
        var sb = new System.Text.StringBuilder();
        sb.Append(y.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)).Append('-')
            .Append(m.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append('-')
            .Append(d.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append('T')
            .Append((rem / 3600).ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
            .Append((rem / 60 % 60).ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
            .Append((rem % 60).ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
        if (nanos != 0)
        {
            sb.Append('.').Append(nanos.ToString("D9", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0'));
        }
        return sb.Append('Z').ToString();
    }

    private static bool Digits(string s, int at, int n, out int value)
    {
        value = 0;
        if (at + n > s.Length) return false;
        for (int i = at; i < at + n; i++)
        {
            if (!char.IsAsciiDigit(s[i])) return false;
            value = value * 10 + (s[i] - '0');
        }
        return true;
    }

    private static bool IsLeap(long y) => y % 4 == 0 && (y % 100 != 0 || y % 400 == 0);

    private static int DaysIn(int y, int m) => m switch
    {
        2 => IsLeap(y) ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    // Days from 0001-01-01 in the proleptic Gregorian calendar; year 0 is
    // negative. Howard Hinnant's days_from_civil, moved to that origin.
    private static long DaysFromYear1(long y, int m, int d)
    {
        y -= m <= 2 ? 1 : 0;
        long era = (y >= 0 ? y : y - 399) / 400;
        long yoe = y - era * 400;
        long doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
        long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 306;
    }

    private static (long Y, int M, int D) CivilFromDays(long z)
    {
        z += 306;
        long era = (z >= 0 ? z : z - 146096) / 146097;
        long doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        long y = yoe + era * 400;
        long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        long mp = (5 * doy + 2) / 153;
        int d = (int)(doy - (153 * mp + 2) / 5 + 1);
        int m = (int)(mp < 10 ? mp + 3 : mp - 9);
        return (y + (m <= 2 ? 1 : 0), m, d);
    }
}

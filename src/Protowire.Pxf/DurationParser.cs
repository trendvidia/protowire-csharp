// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.

namespace Protowire.Pxf;

internal static class DurationParser
{
    private const ulong Limit = 1UL << 63;

    /// <summary>
    /// Parses a duration literal to nanoseconds, by the rules of Go's
    /// time.ParseDuration, which is what the reference validates a literal
    /// with: the value must fit 64 bits, about 292 years, and a fraction is
    /// exact rather than a double's approximation.
    /// </summary>
    public static bool TryParseNanos(string s, out long nanos)
    {
        nanos = 0;
        if (s.Length == 0) return false;
        int i = 0;
        bool neg = false;
        if (s[0] is '-' or '+')
        {
            neg = s[0] == '-';
            i = 1;
        }
        if (s.AsSpan(i) is "0") return true;
        if (i == s.Length) return false;

        ulong total = 0;
        while (i < s.Length)
        {
            if (s[i] != '.' && !char.IsAsciiDigit(s[i])) return false;
            int start = i;
            ulong v = 0;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                if (v > Limit / 10) return false;
                v = v * 10 + (ulong)(s[i++] - '0');
                if (v > Limit) return false;
            }
            bool pre = i > start;
            bool post = false;
            ulong f = 0;
            double scale = 1;
            if (i < s.Length && s[i] == '.')
            {
                i++;
                start = i;
                bool overflow = false;
                while (i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    char c = s[i++];
                    if (overflow) continue;
                    if (f > (Limit - 1) / 10) { overflow = true; continue; }
                    ulong y = f * 10 + (ulong)(c - '0');
                    if (y > Limit) { overflow = true; continue; }
                    f = y;
                    scale *= 10;
                }
                post = i > start;
            }
            if (!pre && !post) return false;

            start = i;
            while (i < s.Length && s[i] != '.' && !char.IsAsciiDigit(s[i])) i++;
            if (i == start) return false;
            ulong unit;
            switch (s[start..i])
            {
                case "ns": unit = 1; break;
                case "us" or "µs" or "μs": unit = 1_000; break;
                case "ms": unit = 1_000_000; break;
                case "s": unit = 1_000_000_000; break;
                case "m": unit = 60_000_000_000; break;
                case "h": unit = 3_600_000_000_000; break;
                default: return false;
            }
            if (v > Limit / unit) return false;
            v *= unit;
            if (f > 0)
            {
                v += (ulong)((double)f * ((double)unit / scale));
                if (v > Limit) return false;
            }
            total += v;
            if (total > Limit) return false;
        }
        if (neg)
        {
            nanos = unchecked(-(long)total);
            return true;
        }
        if (total > long.MaxValue) return false;
        nanos = (long)total;
        return true;
    }

    public static TimeSpan Parse(string s)
    {
        if (string.IsNullOrEmpty(s)) return TimeSpan.Zero;
        if (!TryParseNanos(s, out long nanos)) throw new FormatException($"invalid duration: {s}");
        return TimeSpan.FromTicks(nanos / 100);
    }

    public static string Format(TimeSpan ts)
    {
        if (ts == TimeSpan.Zero) return "0s";
        
        var sb = new System.Text.StringBuilder();
        if (ts < TimeSpan.Zero)
        {
            sb.Append('-');
            ts = -ts;
        }

        long nanos = ts.Ticks * 100;
        if (nanos >= 3600_000_000_000L)
        {
            sb.Append(nanos / 3600_000_000_000L).Append('h');
            nanos %= 3600_000_000_000L;
        }
        if (nanos >= 60_000_000_000L)
        {
            sb.Append(nanos / 60_000_000_000L).Append('m');
            nanos %= 60_000_000_000L;
        }
        if (nanos >= 1_000_000_000L)
        {
            sb.Append(nanos / 1_000_000_000L).Append('s');
            nanos %= 1_000_000_000L;
        }
        if (nanos >= 1_000_000L)
        {
            sb.Append(nanos / 1_000_000L).Append("ms");
            nanos %= 1_000_000L;
        }
        if (nanos >= 1_000L)
        {
            sb.Append(nanos / 1_000L).Append("us");
            nanos %= 1_000L;
        }
        if (nanos > 0)
        {
            sb.Append(nanos).Append("ns");
        }

        return sb.ToString();
    }
}

using System.Globalization;

namespace Protowire.Pxf;

internal static class DurationParser
{
    public static TimeSpan Parse(string s)
    {
        if (string.IsNullOrEmpty(s)) return TimeSpan.Zero;

        bool negative = false;
        if (s[0] == '-')
        {
            negative = true;
            s = s[1..];
        }

        double totalNanos = 0;
        int i = 0;
        while (i < s.Length)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (start == i) throw new Exception($"Invalid duration: {s}");
            
            double val = double.Parse(s[start..i], CultureInfo.InvariantCulture);

            start = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            string unit = s[start..i];

            totalNanos += unit switch
            {
                "h" => val * 3600_000_000_000.0,
                "m" => val * 60_000_000_000.0,
                "s" => val * 1_000_000_000.0,
                "ms" => val * 1_000_000.0,
                "us" or "µs" => val * 1_000.0,
                "ns" => val,
                _ => throw new Exception($"Unknown duration unit: {unit}")
            };
        }

        var ts = TimeSpan.FromTicks((long)(totalNanos / 100));
        return negative ? -ts : ts;
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

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pb;

/// <summary>
/// The skip protobuf-go's <c>protowire.ConsumeFieldValue</c> does, with its
/// error codes and texts, so a field read as unknown is refused exactly where,
/// and in the words, protowire-go refuses it (trendvidia/protowire#614).
/// Google.Protobuf's <c>SkipLastField</c> is not that skip: it consumes
/// nothing for wire type 6 or 7, inside a group too. Protowire.Pxf compiles
/// this file too, for <c>WireCheck</c> (#120).
/// </summary>
internal static class Wire
{
    internal const int Varint = 0, Fixed64 = 1, Bytes = 2, StartGroup = 3, EndGroup = 4, Fixed32 = 5;

    // protowire.DefaultRecursionLimit: how deep groups nest in a skipped value.
    private const int RecursionLimit = 10000;

    internal const int ErrTruncated = -1, ErrFieldNumber = -2, ErrOverflow = -3,
        ErrReserved = -4, ErrEndGroup = -5, ErrRecursionDepth = -6;

    /// <summary>protowire.ParseError's text for a negative length.</summary>
    internal static string ParseError(int code) => code switch
    {
        ErrTruncated => "unexpected EOF",
        ErrFieldNumber => "proto: invalid field number",
        ErrOverflow => "proto: variable length integer overflow",
        ErrReserved => "proto: cannot parse reserved wire type",
        ErrEndGroup => "proto: mismatching end group marker",
        _ => "proto: parse error",
    };

    /// <summary>
    /// The length of the value of a field with this number and wire type at the
    /// start of <paramref name="b"/>, or a negative error code. A group's length
    /// includes its end marker, which must carry the group's number.
    /// </summary>
    internal static int ConsumeFieldValue(long num, int typ, ReadOnlySpan<byte> b)
    {
        if (typ != StartGroup) return ConsumeValue(typ, b);
        // protowire recurses into a nested group; a stack of the open groups'
        // numbers does the same without a frame per level.
        var open = new Stack<long>();
        open.Push(num);
        int pos = 0;
        while (true)
        {
            int n = ConsumeTag(b[pos..], out long num2, out int typ2);
            if (n < 0) return n;
            pos += n;
            if (typ2 == EndGroup)
            {
                if (open.Pop() != num2) return ErrEndGroup;
                if (open.Count == 0) return pos;
                continue;
            }
            if (typ2 == StartGroup)
            {
                if (open.Count > RecursionLimit) return ErrRecursionDepth;
                open.Push(num2);
                continue;
            }
            n = ConsumeValue(typ2, b[pos..]);
            if (n < 0) return n;
            pos += n;
        }
    }

    // A value that is not a group.
    private static int ConsumeValue(int typ, ReadOnlySpan<byte> b)
    {
        switch (typ)
        {
            case Varint:
                return ConsumeVarint(b, out _);
            case Fixed32:
                return b.Length < 4 ? ErrTruncated : 4;
            case Fixed64:
                return b.Length < 8 ? ErrTruncated : 8;
            case Bytes:
                int n = ConsumeVarint(b, out ulong m);
                if (n < 0) return n;
                if (m > (ulong)(b.Length - n)) return ErrTruncated;
                return n + (int)m;
            case EndGroup:
                return ErrEndGroup;
            default:
                return ErrReserved;
        }
    }

    // protowire.ConsumeTag: a field number below 1, or past an int32, is
    // refused.
    internal static int ConsumeTag(ReadOnlySpan<byte> b, out long num, out int typ)
    {
        num = 0;
        typ = 0;
        int n = ConsumeVarint(b, out ulong v);
        if (n < 0) return n;
        if (v >> 3 > int.MaxValue || v >> 3 < 1) return ErrFieldNumber;
        num = (long)(v >> 3);
        typ = (int)(v & 7);
        return n;
    }

    // protowire.ConsumeVarint: at most ten bytes, and the tenth may only hold
    // the top bit.
    internal static int ConsumeVarint(ReadOnlySpan<byte> b, out ulong v)
    {
        v = 0;
        for (int i = 0; i < 10; i++)
        {
            if (i >= b.Length) return ErrTruncated;
            ulong y = b[i];
            if (i == 9)
            {
                if (y >= 2) return ErrOverflow;
                v |= y << 63;
                return 10;
            }
            v |= (y & 0x7f) << (7 * i);
            if (y < 0x80) return i + 1;
        }
        return ErrOverflow; // not reached
    }
}
